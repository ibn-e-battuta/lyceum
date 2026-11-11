using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lyceum.Data;
using Microsoft.EntityFrameworkCore;

namespace Lyceum.Services;

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

    // ---------- retrieval: keywords + meaning ----------

    /// <summary>Transcript passages of roughly 45 seconds, the unit for both keyword and semantic search.</summary>
    private async Task<List<(Lesson Lesson, double Start, string Text)>> PassagesAsync(Course course)
    {
        var lessonsByPath = course.AllLessons.ToDictionary(l => l.RelativePath);
        await using var db = await dbFactory.CreateDbContextAsync();
        var cues = await db.Cues.AsNoTracking().Where(c => lessonsByPath.Keys.Contains(c.LessonPath))
            .OrderBy(c => c.LessonPath).ThenBy(c => c.Start).ToListAsync();
        var passages = new List<(Lesson Lesson, double Start, string Text)>();
        foreach (var g in cues.GroupBy(c => c.LessonPath))
        {
            var sb = new StringBuilder();
            double start = -1;
            foreach (var c in g)
            {
                if (start < 0) start = c.Start;
                sb.Append(c.Text).Append(' ');
                if (c.End - start >= 45) { passages.Add((lessonsByPath[g.Key], start, sb.ToString().Trim())); sb.Clear(); start = -1; }
            }
            if (sb.Length > 0) passages.Add((lessonsByPath[g.Key], start, sb.ToString().Trim()));
        }
        return passages;
    }

    /// <summary>
    /// Finds the passages most related to a question. With an embedding model configured, passages are ranked by
    /// meaning (cosine similarity) blended with a TF-IDF keyword score, so exact terms still count. Otherwise keywords only.
    /// </summary>
    public async Task<List<AiSource>> RetrieveAsync(Course course, string question, int take = 6)
    {
        var passages = await PassagesAsync(course);
        if (passages.Count == 0) return [];

        var keyword = KeywordScores(passages, question);
        var semantic = await SemanticScoresAsync(course, passages, question);

        double Norm(double v, double max) => max <= 0 ? 0 : v / max;
        var kMax = keyword.Length == 0 ? 0 : keyword.Max();
        var ranked = passages.Select((p, i) =>
        {
            var k = Norm(keyword[i], kMax);
            var score = semantic is null ? k : 0.65 * Math.Max(0, semantic[i]) + 0.35 * k;
            return (p, score);
        }).Where(x => x.score > 0).OrderByDescending(x => x.score).Take(take).ToList();

        return ranked.Select((x, i) => new AiSource(i + 1, course, x.p.Lesson, x.p.Start, x.p.Text)).ToList();
    }

    private static double[] KeywordScores(List<(Lesson Lesson, double Start, string Text)> passages, string question)
    {
        var terms = Words(question).Where(w => w.Length > 2 && !StopWords.Contains(w)).Distinct().ToList();
        var scores = new double[passages.Count];
        if (terms.Count == 0) return scores;
        var docFreq = terms.ToDictionary(t => t, t => passages.Count(p => p.Text.Contains(t, StringComparison.OrdinalIgnoreCase)));
        for (var i = 0; i < passages.Count; i++)
        {
            var p = passages[i];
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
            scores[i] = score;
        }
        return scores;
    }

    /// <summary>Cosine similarities, or null when semantic search isn't set up or the course isn't indexed yet.</summary>
    private async Task<double[]?> SemanticScoresAsync(Course course, List<(Lesson Lesson, double Start, string Text)> passages, string question)
    {
        var cfg = await settings.GetAsync<AiSettings>();
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.EmbeddingModel)) return null;

        var byKey = await VectorsAsync(course, cfg.EmbeddingModel!);
        // A vector only counts for the passage it was made from: a re-transcribed lesson keeps its times but not its words.
        float[]? VectorFor((Lesson Lesson, double Start, string Text) p) =>
            byKey.TryGetValue((p.Lesson.RelativePath, Math.Round(p.Start, 1)), out var e) && e.Text == p.Text ? e.Vector : null;
        var missing = passages.Count(p => VectorFor(p) is null);
        if (missing > 0) _ = IndexCourseAsync(course); // keep improving in the background
        if (missing == passages.Count) return null;

        float[] q;
        try { q = (await EmbedAsync([question]))[0]; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Embedding the question failed; using keyword search");
            return null;
        }
        return passages.Select(p => VectorFor(p) is { } v ? Cosine(q, v) : 0).ToArray();
    }

    // Decoded vectors per course, so a question doesn't re-read and re-decode the whole index. Cleared when indexing changes it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Model, Dictionary<(string, double), (string Text, float[] Vector)> Vectors)> _vectors = new();

    private async Task<Dictionary<(string, double), (string Text, float[] Vector)>> VectorsAsync(Course course, string model)
    {
        if (_vectors.TryGetValue(course.Id, out var cached) && cached.Model == model) return cached.Vectors;
        var paths = course.AllLessons.Select(l => l.RelativePath).ToList();
        await using var db = await dbFactory.CreateDbContextAsync();
        var stored = await db.Embeddings.AsNoTracking().Where(e => e.Model == model && paths.Contains(e.LessonPath)).ToListAsync();
        var vectors = stored.GroupBy(e => (e.LessonPath, Math.Round(e.Start, 1)))
            .ToDictionary(g => g.Key, g => (g.First().Text, ToFloats(g.First().Vector)));
        _vectors[course.Id] = (model, vectors);
        return vectors;
    }

    public async Task<List<float[]>> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct = default)
    {
        var (http, cfg) = await ClientAsync();
        using (http)
        {
            var body = new JsonObject
            {
                ["model"] = cfg.EmbeddingModel,
                ["input"] = new JsonArray(inputs.Select(i => (JsonNode)JsonValue.Create(i.Length > 2000 ? i[..2000] : i)).ToArray()),
            };
            using var resp = await http.PostAsync("embeddings", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"The embedding server answered {(int)resp.StatusCode}: {Trim(text)}");
            var data = JsonNode.Parse(text)?["data"] as JsonArray ?? throw new InvalidOperationException("No embeddings returned.");
            return data.OrderBy(d => d?["index"]?.GetValue<int>() ?? 0)
                .Select(d => (d?["embedding"] as JsonArray ?? []).Select(v => v!.GetValue<float>()).ToArray()).ToList();
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int Done, int Total)> _indexing = new();

    /// <summary>Progress of a running semantic index build for a course, if any.</summary>
    public (int Done, int Total)? IndexProgress(Course course) => _indexing.TryGetValue(course.Id, out var p) ? p : null;

    /// <summary>Embeds the course's passages that aren't indexed yet with the configured model. Safe to call repeatedly.</summary>
    public async Task<int> IndexCourseAsync(Course course, CancellationToken ct = default)
    {
        var cfg = await settings.GetAsync<AiSettings>();
        if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.EmbeddingModel)) return 0;
        if (!_indexing.TryAdd(course.Id, (0, 0))) return 0; // already running
        try
        {
            var passages = await PassagesAsync(course);
            var paths = course.AllLessons.Select(l => l.RelativePath).ToList();
            HashSet<(string, double, string)> have;
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                // Drop vectors whose passage changed or is gone (re-transcribed lessons, removed lessons, another model).
                var current = passages.Select(p => (p.Lesson.RelativePath, Math.Round(p.Start, 1), p.Text)).ToHashSet();
                var rows = await db.Embeddings.Where(e => paths.Contains(e.LessonPath))
                    .Select(e => new { e.Id, e.LessonPath, e.Start, e.Text, e.Model }).ToListAsync();
                var stale = rows.Where(e => e.Model == cfg.EmbeddingModel && !current.Contains((e.LessonPath, Math.Round(e.Start, 1), e.Text))).Select(e => e.Id).ToList();
                if (stale.Count > 0)
                {
                    await db.Embeddings.Where(e => stale.Contains(e.Id)).ExecuteDeleteAsync(ct);
                    _vectors.TryRemove(course.Id, out _);
                }
                have = rows.Where(e => e.Model == cfg.EmbeddingModel && !stale.Contains(e.Id)).Select(e => (e.LessonPath, Math.Round(e.Start, 1), e.Text)).ToHashSet();
            }
            var todo = passages.Where(p => !have.Contains((p.Lesson.RelativePath, Math.Round(p.Start, 1), p.Text))).ToList();
            var done = 0;
            _indexing[course.Id] = (0, todo.Count);
            foreach (var batch in todo.Chunk(16))
            {
                var vectors = await EmbedAsync(batch.Select(b => $"{b.Lesson.Title}: {b.Text}").ToList(), ct);
                await using var db = await dbFactory.CreateDbContextAsync();
                for (var i = 0; i < batch.Length && i < vectors.Count; i++)
                    db.Embeddings.Add(new PassageEmbedding
                    {
                        LessonPath = batch[i].Lesson.RelativePath, Start = batch[i].Start, Text = batch[i].Text, Model = cfg.EmbeddingModel!, Vector = ToBytes(vectors[i]),
                    });
                await db.SaveChangesAsync(ct);
                _vectors.TryRemove(course.Id, out _);
                done += batch.Length;
                _indexing[course.Id] = (done, todo.Count);
            }
            if (done > 0) logger.LogInformation("Indexed {Count} passages of {Course} for semantic search", done, course.Title);
            return done;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Semantic indexing of {Course} failed", course.Title);
            return 0;
        }
        finally
        {
            _indexing.TryRemove(course.Id, out _);
        }
    }

    public async Task<(int Indexed, int Total)> IndexStatusAsync(Course course)
    {
        var cfg = await settings.GetAsync<AiSettings>();
        var total = (await PassagesAsync(course)).Count;
        if (string.IsNullOrWhiteSpace(cfg.EmbeddingModel)) return (0, total);
        var paths = course.AllLessons.Select(l => l.RelativePath).ToList();
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.Embeddings.CountAsync(e => e.Model == cfg.EmbeddingModel && paths.Contains(e.LessonPath)), total);
    }

    private static byte[] ToBytes(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] ToFloats(byte[] b)
    {
        var v = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, v, 0, b.Length);
        return v;
    }

    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
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
