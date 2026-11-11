using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using CourseHub.Data;
using Microsoft.EntityFrameworkCore;

namespace CourseHub.Services;

public sealed record Cue(double Start, double End, string Text);

public enum JobState { Queued, Running, Done, Failed }

public sealed record TranscriptJob(string LessonId, string Title, JobState State, int Percent, string? Error, DateTime UpdatedAt);

public sealed record TranscriptHit(Course Course, Lesson Lesson, double Start, string Text);

/// <summary>
/// Subtitles and transcripts: reads .srt/.vtt files next to videos, generates new ones with
/// whisper.cpp in the background, and indexes every cue so transcripts are searchable.
/// </summary>
public sealed partial class TranscriptService(
    CourseLibrary library,
    MediaTools tools,
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

    public string? ToVtt(Lesson lesson)
    {
        var cues = GetCues(lesson);
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

    public async Task<List<TranscriptHit>> SearchAsync(string query, int limit = 40)
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
            if (library.FindByPath(r.LessonPath) is not { } x) continue;
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
        var cues = GetCues(lesson);
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
            }
        }
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

        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "coursehub-" + lesson.Id)).FullName;
        try
        {
            var wav = Path.Combine(work, "audio.wav");
            var (code, err) = await MediaTools.RunAsync(tools.Ffmpeg!, [
                "-hide_banner", "-loglevel", "error", "-y", "-i", lesson.FullPath, "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", wav,
            ], TimeSpan.FromMinutes(20), ct: ct);
            if (code != 0) throw new InvalidOperationException("ffmpeg could not extract audio: " + err.Trim());

            var outBase = Path.Combine(work, "out");
            var threads = Math.Clamp(Environment.ProcessorCount - 2, 2, 8);
            (code, err) = await MediaTools.RunAsync(tools.Whisper!, [
                "-m", tools.ModelPath, "-f", wav, "-l", tools.Language, "-t", threads.ToString(), "-ovtt", "-of", outBase, "-pp",
            ], TimeSpan.FromHours(3), line =>
            {
                var m = Progress().Match(line);
                if (m.Success) Update(lesson, JobState.Running, int.Parse(m.Groups[1].Value), null);
            }, ct);
            if (code != 0 || !File.Exists(outBase + ".vtt"))
                throw new InvalidOperationException("whisper-cli failed: " + err.Trim().Split('\n').LastOrDefault());

            var target = GeneratedPath(lesson);
            File.Move(outBase + ".vtt", target, overwrite: true);
            await IndexAsync(lesson, target, File.GetLastWriteTimeUtc(target).Ticks);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp cleanup */ }
        }
    }

    // ---------- model download ----------

    public async Task DownloadModelAsync()
    {
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
