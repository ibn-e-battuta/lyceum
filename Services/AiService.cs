using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CourseHub.Data;
using Microsoft.EntityFrameworkCore;

namespace CourseHub.Services;

public sealed record QuizQuestion(string Question, List<string> Options, int Answer, string? Explanation);
public sealed record Flashcard(string Front, string Back);
public sealed record AiSource(int Number, Course Course, Lesson Lesson, double Start, string Text);
public sealed record ChatTurn(string Role, string Content);

/// <summary>
/// Study tools built on the lesson transcripts, using any OpenAI-compatible chat API
/// (Ollama, LM Studio, llama.cpp server, vLLM, OpenAI…). Generated summaries, quizzes and
/// flashcards are cached and shared by everyone.
/// </summary>
public sealed partial class AiService(
    AppSettingsService settings,
    TranscriptService transcripts,
    IDbContextFactory<ProgressDb> dbFactory,
    IHttpClientFactory httpFactory,
    ILogger<AiService> logger)
{
    private const int MaxTranscriptChars = 24_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","and","or","but","of","to","in","on","for","with","is","are","was","were","be","it","this","that","what","how",
        "why","when","where","which","who","do","does","did","can","could","should","would","i","you","we","they","he","she","my","your",
        "about","from","as","at","by","if","then","so","not","no","yes","there","their","them","these","those","into","than","also","just",
    };

    public async Task<bool> IsEnabledAsync() => (await settings.GetAsync<AiSettings>()).Enabled;

    // ---------- low level ----------

    private async Task<(HttpClient Http, AiSettings Cfg)> ClientAsync()
    {
        var cfg = await settings.GetAsync<AiSettings>();
        if (!cfg.Enabled) throw new InvalidOperationException("AI features are turned off. An admin can set them up in Settings.");
        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(10); // local models on a CPU can be slow
        http.BaseAddress = new Uri(cfg.BaseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(cfg.ApiKey)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
        return (http, cfg);
    }

    public async Task<string> CompleteAsync(IEnumerable<ChatTurn> messages, bool json = false, CancellationToken ct = default)
    {
        var (http, cfg) = await ClientAsync();
        using (http)
        {
            var body = new JsonObject
            {
                ["model"] = cfg.Model,
                ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
                ["temperature"] = 0.3,
                ["stream"] = false,
            };
            if (json) body["response_format"] = new JsonObject { ["type"] = "json_object" };
            // A sized body (not chunked) works with every OpenAI-compatible server.
            using var resp = await http.PostAsync("chat/completions", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"The AI server answered {(int)resp.StatusCode}: {Trim(text)}");
            return JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                   ?? throw new InvalidOperationException("The AI server returned an empty answer.");
        }
    }

    public async IAsyncEnumerable<string> StreamAsync(IEnumerable<ChatTurn> messages, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (http, cfg) = await ClientAsync();
        using (http)
        {
            var body = new JsonObject
            {
                ["model"] = cfg.Model,
                ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
                ["temperature"] = 0.3,
                ["stream"] = true,
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"The AI server answered {(int)resp.StatusCode}: {Trim(await resp.Content.ReadAsStringAsync(ct))}");
            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:")) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") yield break;
                string? piece = null;
                try { piece = JsonNode.Parse(data)?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>(); }
                catch (JsonException) { /* keep-alive or partial line */ }
                if (!string.IsNullOrEmpty(piece)) yield return piece;
            }
        }
    }

    /// <summary>Checks the connection and model. Returns null when it works, else what went wrong.</summary>
    public async Task<string?> TestAsync()
    {
        try
        {
            var answer = await CompleteAsync([new("user", "Reply with the single word: ready")]);
            return answer.Length > 0 ? null : "Empty answer.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;

    // ---------- transcript helpers ----------

    private string TranscriptText(Lesson lesson)
    {
        var cues = transcripts.GetCues(lesson);
        if (cues.Count == 0) throw new InvalidOperationException("This lesson needs a transcript first (see the Transcript tab).");
        var sb = new StringBuilder();
        foreach (var c in cues) sb.Append(c.Text).Append(' ');
        return sb.ToString();
    }

    /// <summary>Long lessons are summarised in parts first, then the parts are combined.</summary>
    private async Task<string> FitTranscriptAsync(Lesson lesson, CancellationToken ct)
    {
        var text = TranscriptText(lesson);
        if (text.Length <= MaxTranscriptChars) return text;
        var parts = new List<string>();
        for (var i = 0; i < text.Length; i += MaxTranscriptChars)
        {
            var chunk = text.Substring(i, Math.Min(MaxTranscriptChars, text.Length - i));
            parts.Add(await CompleteAsync([
                new("system", "You condense lecture transcripts. Keep every concrete fact, definition, example, command and number."),
                new("user", "Condense this part of a lecture transcript into detailed notes:\n\n" + chunk),
            ], ct: ct));
        }
        return string.Join("\n\n", parts);
    }

    private async Task<string?> CachedAsync(Lesson lesson, string kind)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AiContent.AsNoTracking().Where(a => a.LessonPath == lesson.RelativePath && a.Kind == kind)
            .Select(a => a.Content).FirstOrDefaultAsync();
    }

    private async Task StoreAsync(Lesson lesson, string kind, string content)
    {
        var cfg = await settings.GetAsync<AiSettings>();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.AiContent.Where(a => a.LessonPath == lesson.RelativePath && a.Kind == kind).ExecuteDeleteAsync();
        db.AiContent.Add(new AiContent { LessonPath = lesson.RelativePath, Kind = kind, Content = content, Model = cfg.Model, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    public async Task ForgetAsync(Lesson lesson, string kind)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.AiContent.Where(a => a.LessonPath == lesson.RelativePath && a.Kind == kind).ExecuteDeleteAsync();
    }

    // ---------- summary ----------

    public Task<string?> CachedSummaryAsync(Lesson lesson) => CachedAsync(lesson, "summary");

    public async Task<string> SummaryAsync(Lesson lesson, CancellationToken ct = default)
    {
        if (await CachedAsync(lesson, "summary") is { } cached) return cached;
        var text = await FitTranscriptAsync(lesson, ct);
        var summary = await CompleteAsync([
            new("system", "You write concise study notes from lecture transcripts, in Markdown. Use the transcript's language."),
            new("user", $"""
                Lesson: "{lesson.Title}"

                Write:
                1. A 2–3 sentence overview.
                2. "## Key points" — 4 to 8 bullets with the important ideas, definitions and examples.
                3. "## Takeaways" — 2 to 4 bullets the learner should remember or try.

                Don't invent anything that isn't in the transcript.

                Transcript:
                {text}
                """),
        ], ct: ct);
        await StoreAsync(lesson, "summary", summary.Trim());
        return summary.Trim();
    }

    // ---------- quiz & flashcards ----------

    public async Task<List<QuizQuestion>?> CachedQuizAsync(Lesson lesson) =>
        await CachedAsync(lesson, "quiz") is { } c ? JsonSerializer.Deserialize<List<QuizQuestion>>(c, Json) : null;

    public async Task<List<QuizQuestion>> QuizAsync(Lesson lesson, CancellationToken ct = default)
    {
        if (await CachedQuizAsync(lesson) is { Count: > 0 } cached) return cached;
        var text = await FitTranscriptAsync(lesson, ct);
        var raw = await CompleteAsync([
            new("system", "You write multiple-choice quiz questions that test understanding of a lecture. You reply with JSON only."),
            new("user", $$"""
                Write 5 multiple-choice questions about this lesson ("{{lesson.Title}}"). Each has 4 options and exactly one correct answer.
                Test understanding, not trivia. Use the transcript's language.
                Reply with JSON: {"questions":[{"question":"…","options":["…","…","…","…"],"answer":0,"explanation":"…"}]}
                where "answer" is the 0-based index of the correct option.

                Transcript:
                {{text}}
                """),
        ], json: true, ct);
        var node = ParseJson(raw);
        var list = (node?["questions"] as JsonArray ?? [])
            .Select(q => new QuizQuestion(
                q?["question"]?.GetValue<string>() ?? "",
                (q?["options"] as JsonArray ?? []).Select(o => o?.ToString() ?? "").ToList(),
                q?["answer"] is JsonValue v && v.TryGetValue<int>(out var a) ? a : 0,
                q?["explanation"]?.ToString()))
            .Where(q => q.Question.Length > 0 && q.Options.Count >= 2 && q.Answer >= 0 && q.Answer < q.Options.Count)
            .ToList();
        if (list.Count == 0) throw new InvalidOperationException("The model didn't return usable questions. Try again or use a larger model.");
        await StoreAsync(lesson, "quiz", JsonSerializer.Serialize(list, Json));
        return list;
    }

    public async Task<List<Flashcard>?> CachedFlashcardsAsync(Lesson lesson) =>
        await CachedAsync(lesson, "flashcards") is { } c ? JsonSerializer.Deserialize<List<Flashcard>>(c, Json) : null;

    public async Task<List<Flashcard>> FlashcardsAsync(Lesson lesson, CancellationToken ct = default)
    {
        if (await CachedFlashcardsAsync(lesson) is { Count: > 0 } cached) return cached;
        var text = await FitTranscriptAsync(lesson, ct);
        var raw = await CompleteAsync([
            new("system", "You create flashcards for spaced-repetition study. You reply with JSON only."),
            new("user", $$"""
                Create 8 to 12 flashcards for this lesson ("{{lesson.Title}}"). The front is a short question or term, the back a short, precise answer.
                Use the transcript's language. Reply with JSON: {"cards":[{"front":"…","back":"…"}]}

                Transcript:
                {{text}}
                """),
        ], json: true, ct);
        var cards = (ParseJson(raw)?["cards"] as JsonArray ?? [])
            .Select(c => new Flashcard(c?["front"]?.ToString() ?? "", c?["back"]?.ToString() ?? ""))
            .Where(c => c.Front.Length > 0 && c.Back.Length > 0)
            .ToList();
        if (cards.Count == 0) throw new InvalidOperationException("The model didn't return usable flashcards. Try again or use a larger model.");
        await StoreAsync(lesson, "flashcards", JsonSerializer.Serialize(cards, Json));
        return cards;
    }

    /// <summary>Tab-separated front/back, which Anki imports directly.</summary>
    public static string ToAnki(IEnumerable<Flashcard> cards) =>
        string.Join('\n', cards.Select(c => $"{c.Front.Replace('\t', ' ').Replace('\n', ' ')}\t{c.Back.Replace('\t', ' ').Replace('\n', ' ')}"));

    private static JsonNode? ParseJson(string raw)
    {
        try { return JsonNode.Parse(raw); }
        catch (JsonException)
        {
            // Some models wrap JSON in prose or ```json fences.
            var m = JsonBlock().Match(raw);
            if (!m.Success) return null;
            try { return JsonNode.Parse(m.Value); } catch (JsonException) { return null; }
        }
    }

    [GeneratedRegex(@"\{[\s\S]*\}")] private static partial Regex JsonBlock();

    // ---------- ask the course ----------

    /// <summary>Finds the transcript passages most related to a question (simple TF-IDF over ~45 s windows).</summary>
    public async Task<List<AiSource>> RetrieveAsync(Course course, string question, int take = 6)
    {
        var terms = Words(question).Where(w => w.Length > 2 && !StopWords.Contains(w)).Distinct().ToList();
        if (terms.Count == 0) return [];

        var lessonsByPath = course.AllLessons.ToDictionary(l => l.RelativePath);
        await using var db = await dbFactory.CreateDbContextAsync();
        var cues = await db.Cues.AsNoTracking().Where(c => lessonsByPath.Keys.Contains(c.LessonPath))
            .OrderBy(c => c.LessonPath).ThenBy(c => c.Start).ToListAsync();
        if (cues.Count == 0) return [];

        // Group cues into passages of roughly 45 seconds.
        var passages = new List<(Lesson Lesson, double Start, string Text)>();
        foreach (var g in cues.GroupBy(c => c.LessonPath))
        {
            var sb = new StringBuilder();
            double start = -1;
            foreach (var c in g)
            {
                if (start < 0) start = c.Start;
                sb.Append(c.Text).Append(' ');
                if (c.End - start >= 45) { passages.Add((lessonsByPath[g.Key], start, sb.ToString())); sb.Clear(); start = -1; }
            }
            if (sb.Length > 0) passages.Add((lessonsByPath[g.Key], start, sb.ToString()));
        }

        var docFreq = terms.ToDictionary(t => t, t => passages.Count(p => p.Text.Contains(t, StringComparison.OrdinalIgnoreCase)));
        var scored = passages.Select(p =>
        {
            var words = Words(p.Text).ToList();
            double score = 0;
            foreach (var t in terms)
            {
                var tf = words.Count(w => w.Equals(t, StringComparison.OrdinalIgnoreCase) || w.StartsWith(t, StringComparison.OrdinalIgnoreCase));
                if (tf == 0) continue;
                score += (1 + Math.Log(tf)) * Math.Log(1.0 + passages.Count / (1.0 + docFreq[t]));
            }
            // Mention of the lesson title is a strong hint.
            score += terms.Count(t => p.Lesson.Title.Contains(t, StringComparison.OrdinalIgnoreCase)) * 0.5;
            return (p, score);
        }).Where(x => x.score > 0).OrderByDescending(x => x.score).Take(take).ToList();

        return scored.Select((x, i) => new AiSource(i + 1, course, x.p.Lesson, x.p.Start, x.p.Text.Trim())).ToList();
    }

    public IAsyncEnumerable<string> AskAsync(Course course, string question, IReadOnlyList<AiSource> sources, IReadOnlyList<ChatTurn> history, CancellationToken ct = default)
    {
        var context = new StringBuilder();
        foreach (var s in sources)
            context.Append($"[{s.Number}] Lesson \"{s.Lesson.Title}\" at {Components.Format.Clock(Math.Max(s.Start, 0.5))}:\n{s.Text}\n\n");
        var messages = new List<ChatTurn>
        {
            new("system", $"""
                You are a tutor for the course "{course.Title}". Answer using the numbered transcript excerpts below.
                Cite them like [1] or [2][3] right after the sentences they support. If the excerpts don't contain the answer,
                say so and answer briefly from general knowledge, clearly marked as not from the course.
                Answer in the language of the question. Use Markdown.

                Excerpts:
                {(sources.Count == 0 ? "(none found)" : context.ToString())}
                """),
        };
        messages.AddRange(history.TakeLast(6));
        messages.Add(new("user", question));
        return StreamAsync(messages, ct);
    }

    private static IEnumerable<string> Words(string text) => WordRegex().Matches(text).Select(m => m.Value.ToLowerInvariant());
    [GeneratedRegex(@"[\p{L}\p{N}#+.]+")] private static partial Regex WordRegex();

    // ---------- caption translation ----------

    /// <summary>Translates a lesson's captions into another language, keeping the timing.</summary>
    public async Task TranslateCaptionsAsync(Lesson lesson, string languageCode, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var cues = transcripts.GetCues(lesson);
        if (cues.Count == 0) throw new InvalidOperationException("This lesson needs a transcript first.");
        var languageName = TranscriptService.LanguageName(languageCode);
        var result = new List<Cue>(cues.Count);
        const int batch = 30;
        for (var i = 0; i < cues.Count; i += batch)
        {
            var slice = cues.Skip(i).Take(batch).ToList();
            var lines = new JsonArray(slice.Select(c => (JsonNode)JsonValue.Create(c.Text)).ToArray());
            string[]? translated = null;
            for (var attempt = 0; attempt < 2 && translated is null; attempt++)
            {
                var raw = await CompleteAsync([
                    new("system", $"You translate video captions into {languageName}. Keep technical terms, code and names unchanged. Reply with JSON only."),
                    new("user", $$"""
                        Translate each of these {{slice.Count}} caption lines into {{languageName}}.
                        Reply with JSON {"lines":[…]} containing exactly {{slice.Count}} strings, in the same order.

                        {{lines.ToJsonString()}}
                        """),
                ], json: true, ct);
                var arr = ParseJson(raw)?["lines"] as JsonArray;
                if (arr is not null && arr.Count == slice.Count) translated = arr.Select(n => n?.ToString() ?? "").ToArray();
            }
            // If the model keeps miscounting, keep the original lines for this batch rather than misaligning captions.
            for (var j = 0; j < slice.Count; j++)
                result.Add(slice[j] with { Text = translated is not null && translated[j].Length > 0 ? translated[j] : slice[j].Text });
            progress?.Report((int)(100.0 * Math.Min(i + batch, cues.Count) / cues.Count));
        }
        await transcripts.SaveTranslationAsync(lesson, languageCode, result);
        logger.LogInformation("Translated captions of {Lesson} into {Language}", lesson.RelativePath, languageCode);
    }
}
