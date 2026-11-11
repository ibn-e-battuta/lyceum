using System.Security.Cryptography;

namespace CourseHub.Services;

public enum HealthIssueKind { Broken, Empty, NeedsConversion, Duplicate, MissingTranscript, NoCover }

public sealed record HealthIssue(HealthIssueKind Kind, Course Course, Lesson? Lesson, string Detail, string? Group = null);

public sealed record HealthReport(DateTime CreatedAt, int LessonsChecked, IReadOnlyList<HealthIssue> Issues)
{
    public int Count(HealthIssueKind k) => Issues.Count(i => i.Kind == k);
}

/// <summary>Scans every lesson for problems: unreadable or empty files, formats browsers can't play, duplicates, missing transcripts.</summary>
public sealed class HealthReportService(CourseLibrary library, MediaProbe probe, TranscriptService transcripts, ILogger<HealthReportService> logger)
{
    public HealthReport? Last { get; private set; }
    public (int Done, int Total)? Progress { get; private set; }
    public event Action? Changed;

    public async Task<HealthReport> RunAsync()
    {
        if (Progress is not null) return Last ?? new HealthReport(DateTime.UtcNow, 0, []);
        var items = library.Courses.SelectMany(c => c.AllLessons.Select(l => (Course: c, Lesson: l))).ToList();
        var issues = new System.Collections.Concurrent.ConcurrentBag<HealthIssue>();
        var done = 0;
        Progress = (0, items.Count);
        Changed?.Invoke();
        try
        {
            await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (x, _) =>
            {
                var (course, lesson) = x;
                try
                {
                    var info = new FileInfo(lesson.FullPath);
                    if (!info.Exists) issues.Add(new(HealthIssueKind.Broken, course, lesson, "File is missing."));
                    else if (info.Length == 0) issues.Add(new(HealthIssueKind.Empty, course, lesson, "File is empty (0 bytes)."));
                    else if (lesson.Kind == LessonKind.Video)
                    {
                        var media = probe.Available ? await probe.ProbeAsync(lesson.FullPath) : null;
                        if (probe.Available && media is null) issues.Add(new(HealthIssueKind.Broken, course, lesson, "ffprobe can't read this file; it's probably damaged or incomplete."));
                        else if (media is not null && media.VideoCodec is null) issues.Add(new(HealthIssueKind.Broken, course, lesson, "No video stream found."));
                        else if (media is not null && Transcoder.SafeVariant(media) != PlaybackVariant.Direct)
                            issues.Add(new(HealthIssueKind.NeedsConversion, course, lesson,
                                $"{media.Container} · {media.VideoCodec}{(media.Is10Bit ? " 10-bit" : "")} · {media.AudioCodec ?? "no audio"} — {(Transcoder.SafeVariant(media) == PlaybackVariant.Remux ? "will be repackaged" : "will be converted")} on first play"));
                        if (!transcripts.HasTranscript(lesson)) issues.Add(new(HealthIssueKind.MissingTranscript, course, lesson, "No transcript or subtitles."));
                    }
                }
                catch (Exception ex)
                {
                    issues.Add(new(HealthIssueKind.Broken, course, lesson, ex.Message));
                }
                var n = Interlocked.Increment(ref done);
                if (n % 25 == 0) { Progress = (n, items.Count); Changed?.Invoke(); }
            });

            foreach (var dup in FindDuplicates(items)) issues.Add(dup);
            foreach (var c in library.Courses.Where(c => c.CoverPath is null))
                issues.Add(new(HealthIssueKind.NoCover, c, null, "No cover image (cover.jpg, folder.jpg, poster.png…); a video frame is used instead."));

            Last = new HealthReport(DateTime.UtcNow, items.Count, issues.OrderBy(i => i.Kind).ThenBy(i => i.Course.Title).ThenBy(i => i.Lesson?.RelativePath).ToList());
            logger.LogInformation("Library health check: {Lessons} lessons, {Issues} findings", items.Count, Last.Issues.Count);
            return Last;
        }
        finally
        {
            Progress = null;
            Changed?.Invoke();
        }
    }

    /// <summary>Same size and same first/last 64 KB → almost certainly the same file.</summary>
    private static IEnumerable<HealthIssue> FindDuplicates(List<(Course Course, Lesson Lesson)> items)
    {
        foreach (var bySize in items.Where(x => x.Lesson.SizeBytes > 0).GroupBy(x => x.Lesson.SizeBytes).Where(g => g.Count() > 1))
        {
            foreach (var byHash in bySize.GroupBy(x => Fingerprint(x.Lesson.FullPath)).Where(g => g.Key is not null && g.Count() > 1))
            {
                var group = byHash.Key![..8];
                var list = byHash.ToList();
                foreach (var x in list)
                {
                    var others = string.Join(", ", list.Where(o => o.Lesson != x.Lesson).Select(o => $"{o.Course.Title} › {o.Lesson.Title}"));
                    yield return new HealthIssue(HealthIssueKind.Duplicate, x.Course, x.Lesson, $"Same file as {others} ({Components.Format.Bytes(x.Lesson.SizeBytes)} each).", group);
                }
            }
        }
    }

    private static string? Fingerprint(string path)
    {
        try
        {
            const int chunk = 64 * 1024;
            using var fs = File.OpenRead(path);
            var buf = new byte[chunk * 2];
            var read = fs.Read(buf, 0, chunk);
            if (fs.Length > chunk * 2) fs.Seek(-chunk, SeekOrigin.End);
            read += fs.Read(buf, read, chunk);
            return Convert.ToHexStringLower(SHA256.HashData(buf.AsSpan(0, read)));
        }
        catch
        {
            return null;
        }
    }
}
