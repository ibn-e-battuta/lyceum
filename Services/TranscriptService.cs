using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Lyceum.Data;
using Microsoft.EntityFrameworkCore;

namespace Lyceum.Services;

public sealed record Cue(double Start, double End, string Text);

public enum JobState { Queued, Running, Done, Failed }

public sealed record TranscriptJob(string LessonId, string Title, JobState State, int Percent, string? Error, DateTime UpdatedAt);

public sealed record TranscriptHit(Course Course, Lesson Lesson, double Start, string Text);

/// <summary>A caption track: the original transcript, or a translation.</summary>
public sealed record CaptionTrack(string Language, string Label, string File, bool IsOriginal);

/// <summary>
/// Subtitles and transcripts: reads .srt/.vtt files next to videos, generates new ones with
/// whisper.cpp in the background, and indexes every cue so transcripts are searchable.
/// </summary>
public sealed partial class TranscriptService(
    CourseLibrary library,
    MediaTools tools,
    AppSettingsService settings,
    IDbContextFactory<ProgressDb> dbFactory,
    IHttpClientFactory httpFactory,
    ILogger<TranscriptService> logger) : BackgroundService
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, TranscriptJob> _jobs = new();
    private readonly ConcurrentDictionary<string, (long Ticks, IReadOnlyList<Cue> Cues)> _cueCache = new();
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private CancellationTokenSource? _currentJob;

    public event Action? Changed;
    /// <summary>Raised when the queue empties, with the number of lessons finished since it last did.</summary>
    public event Action<int>? QueueDrained;
    private int _doneSinceDrain;

    public IReadOnlyCollection<TranscriptJob> Jobs => _jobs.Values.OrderBy(j => j.State == JobState.Running ? 0 : j.State == JobState.Queued ? 1 : 2).ThenByDescending(j => j.UpdatedAt).ToList();
    public TranscriptJob? JobFor(Lesson lesson) => _jobs.GetValueOrDefault(lesson.Id);
    public (double Percent, long Bytes, string? Error)? ModelDownload { get; private set; }

    // ---------- reading ----------

    public string GeneratedPath(Lesson lesson) => Path.Combine(tools.TranscriptDir, lesson.Id + ".vtt");

    /// <summary>The subtitle file for a lesson: one shipped next to the video wins over a generated one.</summary>
    public string? SubtitleFile(Lesson lesson)
    {
        if (lesson.SubtitlePath is not null && File.Exists(lesson.SubtitlePath)) return lesson.SubtitlePath;
        var generated = GeneratedPath(lesson);
        return File.Exists(generated) ? generated : null;
    }

    public bool HasTranscript(Lesson lesson) => SubtitleFile(lesson) is not null;

    public string TranslationPath(Lesson lesson, string language) => Path.Combine(tools.TranscriptDir, $"{lesson.Id}.{language}.vtt");

    /// <summary>Language of the original transcript ("de", "en"…), when known.</summary>
    public string? OriginalLanguage(Lesson lesson)
    {
        var file = SubtitleFile(lesson);
        if (file is null) return null;
        var marker = file + ".lang";
        if (File.Exists(marker)) return File.ReadAllText(marker).Trim();
        // Transcripts generated before language detection existed were always English.
        if (file.StartsWith(tools.TranscriptDir, StringComparison.Ordinal)) return "en";
        // Sidecar files named "Lesson.de.srt"
        var parts = Path.GetFileNameWithoutExtension(file).Split('.');
        return parts.Length > 1 && parts[^1].Length is 2 or 3 ? parts[^1].ToLowerInvariant() : null;
    }

    public IReadOnlyList<CaptionTrack> Tracks(Lesson lesson)
    {
        var original = SubtitleFile(lesson);
        if (original is null) return [];
        var lang = OriginalLanguage(lesson) ?? "und";
        var tracks = new List<CaptionTrack> { new(lang, LanguageName(lang) + (lang == "und" ? "" : " (original)"), original, true) };
        foreach (var f in Directory.EnumerateFiles(tools.TranscriptDir, lesson.Id + ".*.vtt"))
        {
            var code = Path.GetFileNameWithoutExtension(f)[(lesson.Id.Length + 1)..];
            if (code != lang) tracks.Add(new CaptionTrack(code, LanguageName(code), f, false));
        }
        return tracks;
    }

    public static string LanguageName(string code)
    {
        if (code == "und") return "Transcript";
        try { return System.Globalization.CultureInfo.GetCultureInfo(code).EnglishName; }
        catch (System.Globalization.CultureNotFoundException) { return code; }
    }

    public IReadOnlyList<Cue> GetCues(Lesson lesson, string? language)
    {
        if (language is null) return GetCues(lesson);
        var track = Tracks(lesson).FirstOrDefault(t => t.Language == language);
        return track is null ? GetCues(lesson) : CuesFrom(track.File);
    }

    private IReadOnlyList<Cue> CuesFrom(string file)
    {
        var ticks = File.GetLastWriteTimeUtc(file).Ticks;
        if (_cueCache.TryGetValue(file, out var c) && c.Ticks == ticks) return c.Cues;
        var cues = Parse(File.ReadAllText(file));
        _cueCache[file] = (ticks, cues);
        return cues;
    }

    public async Task SaveTranslationAsync(Lesson lesson, string language, IReadOnlyList<Cue> cues)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in cues)
            sb.Append(Stamp(cue.Start)).Append(" --> ").Append(Stamp(cue.End)).Append('\n').Append(cue.Text).Append("\n\n");
        await File.WriteAllTextAsync(TranslationPath(lesson, language), sb.ToString());
        Changed?.Invoke();
    }

    public IReadOnlyList<Cue> GetCues(Lesson lesson)
    {
        var file = SubtitleFile(lesson);
        if (file is null) return [];
        var ticks = File.GetLastWriteTimeUtc(file).Ticks;
        if (_cueCache.TryGetValue(file, out var c) && c.Ticks == ticks) return c.Cues;
        var cues = Parse(File.ReadAllText(file));
        _cueCache[file] = (ticks, cues);
        return cues;
    }

    public string? ToVtt(Lesson lesson, string? language = null)
    {
        var cues = GetCues(lesson, language);
        if (cues.Count == 0) return null;
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in cues)
            sb.Append(Stamp(cue.Start)).Append(" --> ").Append(Stamp(cue.End)).Append('\n').Append(cue.Text).Append("\n\n");
        return sb.ToString();
    }

    private static string Stamp(double s)
    {
        var t = TimeSpan.FromSeconds(s);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
    }

    /// <summary>Parses SRT or WebVTT.</summary>
    public static IReadOnlyList<Cue> Parse(string text)
    {
        var cues = new List<Cue>();
        var blocks = BlockSplit().Split(text.Replace("\r\n", "\n").Trim());
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var timeLine = Array.FindIndex(lines, l => l.Contains("-->"));
            if (timeLine < 0) continue;
            var m = TimeLine().Match(lines[timeLine]);
            if (!m.Success) continue;
            var body = string.Join(" ", lines.Skip(timeLine + 1).Select(l => Tags().Replace(l, "").Trim()).Where(l => l.Length > 0));
            if (body.Length == 0) continue;
            cues.Add(new Cue(ParseTime(m.Groups[1].Value), ParseTime(m.Groups[2].Value), System.Net.WebUtility.HtmlDecode(body)));
        }
        return cues;
    }

    private static double ParseTime(string s)
    {
        var parts = s.Replace(',', '.').Split(':');
        double total = 0;
        foreach (var p in parts) total = total * 60 + double.Parse(p, CultureInfo.InvariantCulture);
        return total;
    }

    [GeneratedRegex(@"\n\s*\n")] private static partial Regex BlockSplit();
    [GeneratedRegex(@"((?:\d+:)?\d{1,2}:\d{2}[\.,]\d{1,3})\s*-->\s*((?:\d+:)?\d{1,2}:\d{2}[\.,]\d{1,3})")] private static partial Regex TimeLine();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"progress\s*=\s*(\d+)%")] private static partial Regex Progress();

    // ---------- search ----------

    public async Task<List<TranscriptHit>> SearchAsync(string query, AccessScope? allowedLibraries, int limit = 40)
    {
        query = query.Trim();
        if (query.Length < 3) return [];
        await using var db = await dbFactory.CreateDbContextAsync();
        var pattern = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var rows = await db.Cues.AsNoTracking()
            .Where(c => EF.Functions.Like(c.Text, pattern, "\\"))
            .OrderBy(c => c.LessonPath).ThenBy(c => c.Start)
            .Take(limit * 4)
            .ToListAsync();
        var hits = new List<TranscriptHit>();
        foreach (var r in rows)
        {
            if (library.FindByPath(r.LessonPath) is not { } x || !CourseLibrary.CanAccess(x.Course, allowedLibraries)) continue;
            // At most 3 hits per lesson so one lecture doesn't fill the page.
            if (hits.Count(h => h.Lesson.Id == x.Lesson.Id) >= 3) continue;
            hits.Add(new TranscriptHit(x.Course, x.Lesson, r.Start, r.Text));
            if (hits.Count >= limit) break;
        }
        return hits;
    }

    public async Task<int> IndexedLessonCountAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.TranscriptSources.CountAsync();
    }

    /// <summary>Indexes every lesson whose subtitle file is new or changed.</summary>
    public async Task IndexAllAsync()
    {
        await _indexLock.WaitAsync();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var known = await db.TranscriptSources.AsNoTracking().ToDictionaryAsync(s => s.LessonPath);
            var count = 0;
            foreach (var lesson in library.Courses.SelectMany(c => c.AllLessons).Where(l => l.Kind == LessonKind.Video))
            {
                var file = SubtitleFile(lesson);
                if (file is null) continue;
                var ticks = File.GetLastWriteTimeUtc(file).Ticks;
                if (known.TryGetValue(lesson.RelativePath, out var k) && k.FilePath == file && k.FileTicks == ticks) continue;
                await IndexAsync(lesson, file, ticks);
                count++;
            }
            if (count > 0) logger.LogInformation("Indexed transcripts for {Count} lessons", count);
        }
        finally
        {
            _indexLock.Release();
        }
    }

    private async Task IndexAsync(Lesson lesson, string file, long ticks)
    {
        var cues = GetCues(lesson).ToList();
        // Non-English lessons: also index the English translation so they show up in English searches.
        var english = TranslationPath(lesson, "en");
        if (File.Exists(english) && OriginalLanguage(lesson) is { } l && l != "en") cues.AddRange(CuesFrom(english));
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Cues.Where(c => c.LessonPath == lesson.RelativePath).ExecuteDeleteAsync();
        db.Cues.AddRange(cues.Select(c => new TranscriptCue { LessonPath = lesson.RelativePath, Start = c.Start, End = c.End, Text = c.Text }));
        var src = await db.TranscriptSources.FindAsync(lesson.RelativePath);
        if (src is null) db.TranscriptSources.Add(src = new TranscriptSource { LessonPath = lesson.RelativePath });
        src.FilePath = file;
        src.FileTicks = ticks;
        await db.SaveChangesAsync();
    }

    // ---------- generating ----------

    public bool Enqueue(Lesson lesson)
    {
        if (lesson.Kind != LessonKind.Video || HasTranscript(lesson)) return false;
        if (_jobs.TryGetValue(lesson.Id, out var j) && j.State is JobState.Queued or JobState.Running) return false;
        _jobs[lesson.Id] = new TranscriptJob(lesson.Id, lesson.Title, JobState.Queued, 0, null, DateTime.UtcNow);
        _queue.Writer.TryWrite(lesson.Id);
        Changed?.Invoke();
        return true;
    }

    public int EnqueueCourse(Course course) => course.AllLessons.Count(Enqueue);

    public int EnqueueAll() => library.Courses.Sum(EnqueueCourse);

    public void CancelAll()
    {
        foreach (var j in _jobs.Values.Where(j => j.State == JobState.Queued)) _jobs.TryRemove(j.LessonId, out _);
        _currentJob?.Cancel();
        Changed?.Invoke();
    }

    public void ClearFinished()
    {
        foreach (var j in _jobs.Values.Where(j => j.State is JobState.Done or JobState.Failed)) _jobs.TryRemove(j.LessonId, out _);
        Changed?.Invoke();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        library.Changed += () => _ = IndexAllAsync();
        library.Diff += async diff =>
        {
            if (!(await settings.GetAsync<ServerSettings>()).AutoTranscribeNewVideos || !tools.CanTranscribe) return;
            var n = diff.AddedLessons.Count(x => Enqueue(x.Lesson));
            if (n > 0) logger.LogInformation("Queued {Count} new lessons for transcription", n);
        };
        await ApplySettingsAsync();
        settings.Changed += () => _ = ApplySettingsAsync();
        try { await IndexAllAsync(); } catch (Exception ex) { logger.LogWarning(ex, "Initial transcript indexing failed"); }

        await foreach (var lessonId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_jobs.TryGetValue(lessonId, out var job) || job.State != JobState.Queued) continue;
            var lesson = library.GetLesson(lessonId);
            if (lesson is null) { _jobs.TryRemove(lessonId, out _); continue; }

            _currentJob = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            try
            {
                await TranscribeAsync(lesson, _currentJob.Token);
                Update(lesson, JobState.Done, 100, null);
                _doneSinceDrain++;
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                _jobs.TryRemove(lessonId, out _);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Transcription failed for {Lesson}", lesson.RelativePath);
                Update(lesson, JobState.Failed, 0, ex.Message);
            }
            finally
            {
                _currentJob = null;
                Changed?.Invoke();
                if (_queue.Reader.Count == 0 && !_jobs.Values.Any(j => j.State == JobState.Queued) && _doneSinceDrain > 0)
                {
                    var n = _doneSinceDrain;
                    _doneSinceDrain = 0;
                    try { QueueDrained?.Invoke(n); } catch (Exception ex) { logger.LogWarning(ex, "QueueDrained handler failed"); }
                }
            }
        }
    }

    private async Task ApplySettingsAsync()
    {
        var s = await settings.GetAsync<ServerSettings>();
        tools.ModelOverride = s.WhisperModel;
        tools.LanguageOverride = s.WhisperLanguage;
    }

    private void Update(Lesson lesson, JobState state, int percent, string? error)
    {
        _jobs[lesson.Id] = new TranscriptJob(lesson.Id, lesson.Title, state, percent, error, DateTime.UtcNow);
        Changed?.Invoke();
    }

    private async Task TranscribeAsync(Lesson lesson, CancellationToken ct)
    {
        if (!tools.CanTranscribe) throw new InvalidOperationException("ffmpeg, whisper-cli and the Whisper model are all required.");
        Update(lesson, JobState.Running, 0, null);

        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "lyceum-" + lesson.Id)).FullName;
        try
        {
            var wav = Path.Combine(work, "audio.wav");
            var (code, err) = await MediaTools.RunAsync(tools.Ffmpeg!, [
                "-hide_banner", "-loglevel", "error", "-y", "-i", lesson.FullPath, "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wav,
            ], TimeSpan.FromMinutes(20), ct: ct);
            if (code != 0) throw new InvalidOperationException("ffmpeg could not extract audio: " + err.Trim());

            var language = tools.ModelIsMultilingual ? tools.Language : "en";
            var translate = (await settings.GetAsync<ServerSettings>()).TranslateToEnglish && tools.ModelIsMultilingual;
            string? detected = language == "auto" ? null : language;

            // Pass 1: transcript in the spoken language. Pass 2 (optional): Whisper's own translation to English.
            var outBase = Path.Combine(work, "out");
            await RunWhisperAsync(lesson, wav, outBase, language, translate: false, translate ? 0 : 0, translate ? 50 : 100,
                l => detected ??= l, ct);
            var target = GeneratedPath(lesson);
            File.Move(outBase + ".vtt", target, overwrite: true);
            await File.WriteAllTextAsync(target + ".lang", detected ?? "en", ct);

            if (translate && detected is not null && detected != "en")
            {
                var enBase = Path.Combine(work, "en");
                await RunWhisperAsync(lesson, wav, enBase, detected, translate: true, 50, 100, _ => { }, ct);
                File.Move(enBase + ".vtt", TranslationPath(lesson, "en"), overwrite: true);
            }
            await IndexAsync(lesson, target, File.GetLastWriteTimeUtc(target).Ticks);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    private async Task RunWhisperAsync(Lesson lesson, string wav, string outBase, string language, bool translate,
        int fromPercent, int toPercent, Action<string> onDetected, CancellationToken ct)
    {
        // Small boards (Raspberry Pi: 4 cores) use every core; bigger machines keep two free for everything else.
        var threads = Math.Clamp(Environment.ProcessorCount <= 4 ? Environment.ProcessorCount : Environment.ProcessorCount - 2, 1, 8);
        var args = new List<string> { "-m", tools.ModelPath, "-f", wav, "-l", language, "-t", threads.ToString(), "-ovtt", "-of", outBase, "-pp" };
        if (translate) args.Add("-tr");
        var (code, err) = await MediaTools.RunAsync(tools.Whisper!, args, TimeSpan.FromHours(3), line =>
        {
            var m = Progress().Match(line);
            if (m.Success) Update(lesson, JobState.Running, fromPercent + int.Parse(m.Groups[1].Value) * (toPercent - fromPercent) / 100, null);
            var d = Detected().Match(line);
            if (d.Success) onDetected(d.Groups[1].Value);
        }, ct);
        if (code != 0 || !File.Exists(outBase + ".vtt"))
            throw new InvalidOperationException("whisper-cli failed: " + err.Trim().Split('\n').LastOrDefault());
    }

    [GeneratedRegex(@"auto-detected language:\s*([a-z]{2,3})")] private static partial Regex Detected();

    // ---------- model download ----------

    public async Task DownloadModelAsync()
    {
        tools.ModelOverride = (await settings.GetAsync<ServerSettings>()).WhisperModel;
        if (ModelDownload is { Error: null, Percent: < 100 }) return; // already running
        ModelDownload = (0, 0, null);
        Changed?.Invoke();
        var part = tools.ModelPath + ".part";
        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromHours(1);
            using var resp = await http.GetAsync(tools.ModelUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(part))
            {
                var buf = new byte[1 << 16];
                long done = 0;
                var lastTick = DateTime.UtcNow;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if ((DateTime.UtcNow - lastTick).TotalMilliseconds > 500)
                    {
                        lastTick = DateTime.UtcNow;
                        ModelDownload = (total > 0 ? 100.0 * done / total : 0, done, null);
                        Changed?.Invoke();
                    }
                }
            }
            File.Move(part, tools.ModelPath, overwrite: true);
            ModelDownload = (100, new FileInfo(tools.ModelPath).Length, null);
        }
        catch (Exception ex)
        {
            try { File.Delete(part); } catch { /* ignore */ }
            ModelDownload = (0, 0, ex.Message);
        }
        Changed?.Invoke();
    }
}
