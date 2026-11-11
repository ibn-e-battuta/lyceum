using System.Diagnostics;
using System.Globalization;
using System.Text;
using CourseHub.Data;
using Microsoft.EntityFrameworkCore;

namespace CourseHub.Services;

/// <summary>Prometheus text exposition for /metrics (enable with CourseHub__Metrics__Enabled; optional bearer token).</summary>
public sealed class MetricsService(
    CourseLibrary library,
    IDbContextFactory<ProgressDb> dbFactory,
    PresenceService presence,
    TranscriptService transcripts,
    Transcoder transcoder,
    MediaTools tools,
    BackupService backups)
{
    // Walking the storage folders is slow on a Pi with thousands of files, and the sizes change slowly,
    // so scrapes (often every 15 s) reuse a total that's at most five minutes old.
    private (DateTime At, long Thumbs, long Transcodes, long Transcripts, long Models)? _sizes;

    private (long Thumbs, long Transcodes, long Transcripts, long Models) StorageSizes()
    {
        if (_sizes is { } c && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(5)) return (c.Thumbs, c.Transcodes, c.Transcripts, c.Models);
        static long Size(string dir) => Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        var now = (DateTime.UtcNow, Size(tools.ThumbDir), Size(tools.TranscodeDir), Size(tools.TranscriptDir), Size(tools.ModelDir));
        _sizes = now;
        return (now.Item2, now.Item3, now.Item4, now.Item5);
    }

    public async Task<string> RenderAsync()
    {
        var sb = new StringBuilder();
        void Metric(string name, string type, string help, IEnumerable<(string Labels, double Value)> samples)
        {
            sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
            foreach (var (labels, value) in samples)
                sb.Append(name).Append(labels.Length > 0 ? "{" + labels + "}" : "").Append(' ').Append(value.ToString("0.###", CultureInfo.InvariantCulture)).Append('\n');
        }
        static string L(params (string K, string V)[] kv) => string.Join(',', kv.Select(x => $"{x.K}=\"{x.V.Replace("\\", "\\\\").Replace("\"", "\\\"")}\""));

        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0";
        Metric("coursehub_build_info", "gauge", "Version of CourseHub.", [(L(("version", version)), 1)]);
        Metric("coursehub_courses", "gauge", "Courses per library.", library.Libraries.Select(l => (L(("library", l.Name)), (double)library.Courses.Count(c => c.LibraryId == l.Id))));
        Metric("coursehub_lessons", "gauge", "Lessons per library.", library.Libraries.Select(l => (L(("library", l.Name)), (double)library.Courses.Where(c => c.LibraryId == l.Id).Sum(c => c.LessonCount))));
        Metric("coursehub_video_seconds", "gauge", "Total video length per library.", library.Libraries.Select(l => (L(("library", l.Name)), library.Courses.Where(c => c.LibraryId == l.Id).Sum(c => c.TotalSeconds))));
        Metric("coursehub_library_bytes", "gauge", "Size of course files per library.", library.Libraries.Select(l => (L(("library", l.Name)), (double)library.Courses.Where(c => c.LibraryId == l.Id).Sum(c => c.TotalBytes))));
        Metric("coursehub_last_scan_timestamp_seconds", "gauge", "Unix time of the last library scan.", [("", new DateTimeOffset(library.LastScan).ToUnixTimeSeconds())]);

        await using var db = await dbFactory.CreateDbContextAsync();
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        Metric("coursehub_users", "gauge", "Accounts (guests excluded).", [("", await db.Users.CountAsync(u => !u.IsGuest))]);
        Metric("coursehub_active_guest_links", "gauge", "Guest links that haven't expired.", [("", await db.GuestLinks.CountAsync(g => g.ExpiresAt > DateTime.UtcNow))]);
        Metric("coursehub_viewers", "gauge", "People who played something in the last 2 minutes.", [("", presence.Active(TimeSpan.FromMinutes(2)).Count)]);
        Metric("coursehub_watch_seconds_today", "gauge", "Seconds watched today, all users.", [("", await db.WatchDays.Where(w => w.Day == today).SumAsync(w => (double?)w.Seconds) ?? 0)]);
        Metric("coursehub_watch_seconds_total", "counter", "Seconds watched since install, all users.", [("", await db.WatchDays.SumAsync(w => (double?)w.Seconds) ?? 0)]);
        Metric("coursehub_lessons_completed_total", "counter", "Lesson completions recorded.", [("", await db.Activity.CountAsync(a => a.Type == ActivityType.LessonCompleted))]);
        Metric("coursehub_courses_completed_total", "counter", "Course completions recorded.", [("", await db.Activity.CountAsync(a => a.Type == ActivityType.CourseCompleted))]);
        Metric("coursehub_transcribed_lessons", "gauge", "Lessons with a searchable transcript.", [("", await db.TranscriptSources.CountAsync())]);
        Metric("coursehub_review_cards_due", "gauge", "Flashcards due for review, all users.", [("", await db.ReviewCards.CountAsync(c => c.Due <= DateTime.UtcNow))]);

        Metric("coursehub_transcription_jobs", "gauge", "Transcription jobs by state.", Enum.GetValues<JobState>().Select(s => (L(("state", s.ToString().ToLowerInvariant())), (double)transcripts.Jobs.Count(j => j.State == s))));
        Metric("coursehub_conversion_jobs", "gauge", "Video conversion jobs by state.", Enum.GetValues<JobState>().Select(s => (L(("state", s.ToString().ToLowerInvariant())), (double)transcoder.Jobs.Count(j => j.State == s))));

        var sizes = StorageSizes();
        Metric("coursehub_storage_bytes", "gauge", "CourseHub's own data by kind.",
        [
            (L(("kind", "thumbnails")), sizes.Thumbs), (L(("kind", "transcodes")), sizes.Transcodes),
            (L(("kind", "transcripts")), sizes.Transcripts), (L(("kind", "models")), sizes.Models),
            (L(("kind", "backups")), backups.List().Sum(b => b.Size)),
        ]);
        var lastBackup = backups.List().FirstOrDefault();
        Metric("coursehub_last_backup_timestamp_seconds", "gauge", "Unix time of the newest backup (0 = none).",
            [("", lastBackup is null ? 0 : new DateTimeOffset(lastBackup.CreatedAt).ToUnixTimeSeconds())]);

        var proc = Process.GetCurrentProcess();
        Metric("process_resident_memory_bytes", "gauge", "Resident memory.", [("", proc.WorkingSet64)]);
        Metric("process_cpu_seconds_total", "counter", "CPU time used.", [("", proc.TotalProcessorTime.TotalSeconds)]);
        Metric("process_start_time_seconds", "gauge", "Process start time.", [("", new DateTimeOffset(proc.StartTime.ToUniversalTime()).ToUnixTimeSeconds())]);
        Metric("dotnet_gc_heap_bytes", "gauge", ".NET managed heap size.", [("", GC.GetTotalMemory(false))]);
        return sb.ToString();
    }
}
