using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;

namespace Lyceum.Services;

public enum PlaybackVariant { Direct, Remux, H264, Audio }

public sealed record PrepareJob(string LessonId, string Title, PlaybackVariant Variant, JobState State, int Percent, string? Error, DateTime UpdatedAt);

/// <summary>
/// Makes videos playable in any browser. "Remux" copies the video into an MP4 container (fast, lossless);
/// "H264" re-encodes, using hardware encoders when configured. Results are cached in storage/transcodes,
/// trimmed to a size limit by least-recently-watched.
/// </summary>
public sealed class Transcoder(
    MediaTools tools,
    MediaProbe probe,
    CourseLibrary library,
    AppSettingsService settings,
    ILogger<Transcoder> logger) : BackgroundService
{
    private readonly Channel<(string LessonId, PlaybackVariant Variant)> _fast = Channel.CreateUnbounded<(string, PlaybackVariant)>();
    private readonly Channel<(string LessonId, PlaybackVariant Variant)> _slow = Channel.CreateUnbounded<(string, PlaybackVariant)>();
    private readonly ConcurrentDictionary<string, PrepareJob> _jobs = new();

    public event Action? Changed;

    public IReadOnlyCollection<PrepareJob> Jobs => _jobs.Values.OrderBy(j => j.State == JobState.Running ? 0 : j.State == JobState.Queued ? 1 : 2).ThenByDescending(j => j.UpdatedAt).ToList();

    private static string JobKey(string lessonId, PlaybackVariant v) => $"{lessonId}.{v.ToString().ToLowerInvariant()}";

    public string PreparedPath(Lesson lesson, PlaybackVariant v) => Path.Combine(tools.TranscodeDir, JobKey(lesson.Id, v) + ".mp4");

    public string? PreparedFile(Lesson lesson, PlaybackVariant v)
    {
        var p = PreparedPath(lesson, v);
        return File.Exists(p) && File.GetLastWriteTimeUtc(p) >= File.GetLastWriteTimeUtc(lesson.FullPath) ? p : null;
    }

    public PrepareJob? JobFor(Lesson lesson, PlaybackVariant v) => _jobs.GetValueOrDefault(JobKey(lesson.Id, v));

    public bool Enqueue(Lesson lesson, PlaybackVariant variant)
    {
        if (variant == PlaybackVariant.Direct || tools.Ffmpeg is null || PreparedFile(lesson, variant) is not null) return false;
        var key = JobKey(lesson.Id, variant);
        if (_jobs.TryGetValue(key, out var j) && j.State is JobState.Queued or JobState.Running) return false;
        _jobs[key] = new PrepareJob(lesson.Id, lesson.Title, variant, JobState.Queued, 0, null, DateTime.UtcNow);
        (variant is PlaybackVariant.Remux or PlaybackVariant.Audio ? _fast : _slow).Writer.TryWrite((lesson.Id, variant));
        Changed?.Invoke();
        return true;
    }

    /// <summary>Server-side guess used for background preparation, where no browser is around to ask.</summary>
    public static PlaybackVariant SafeVariant(MediaInfo info)
    {
        if (info.VideoCodec == "h264" && !info.Is10Bit)
            return info.IsMp4Family && info.AudioCodec is null or "aac" or "mp3" ? PlaybackVariant.Direct : PlaybackVariant.Remux;
        if (info.IsWebm && info.VideoCodec is "vp8" or "vp9") return PlaybackVariant.Direct;
        return PlaybackVariant.H264; // HEVC/AV1 aren't supported everywhere; MPEG-4/WMV/10-bit nowhere
    }

    /// <summary>Queues every video in a course that a typical browser couldn't play as-is.</summary>
    public async Task<int> PrepareCourseAsync(Course course)
    {
        var n = 0;
        foreach (var l in course.AllLessons.Where(l => l.Kind == LessonKind.Video))
        {
            if (await probe.ProbeAsync(l.FullPath) is { } info && SafeVariant(info) is var v && v != PlaybackVariant.Direct && Enqueue(l, v)) n++;
        }
        return n;
    }

    public void ClearFinished()
    {
        foreach (var j in _jobs.Values.Where(j => j.State is JobState.Done or JobState.Failed)) _jobs.TryRemove(JobKey(j.LessonId, j.Variant), out _);
        Changed?.Invoke();
    }

    /// <summary>Marks a prepared file as recently watched so cache trimming keeps it.</summary>
    public void Touch(string file)
    {
        try { File.SetLastAccessTimeUtc(file, DateTime.UtcNow); } catch { /* read-only fs */ }
    }

    public long CacheBytes() => Directory.EnumerateFiles(tools.TranscodeDir, "*.mp4").Sum(f => new FileInfo(f).Length);

    public void ClearCache()
    {
        foreach (var f in Directory.EnumerateFiles(tools.TranscodeDir)) { try { File.Delete(f); } catch { /* in use */ } }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        library.Diff += async diff =>
        {
            if (!(await settings.GetAsync<ServerSettings>()).AutoPrepareVideos) return;
            foreach (var (_, lesson) in diff.AddedLessons.Where(x => x.Lesson.Kind == LessonKind.Video))
                if (await probe.ProbeAsync(lesson.FullPath) is { } info && SafeVariant(info) is var v && v != PlaybackVariant.Direct)
                    Enqueue(lesson, v);
        };

        await Task.WhenAll(Worker(_fast.Reader, stoppingToken), Worker(_slow.Reader, stoppingToken));
    }

    private async Task Worker(ChannelReader<(string LessonId, PlaybackVariant Variant)> reader, CancellationToken ct)
    {
        await foreach (var (lessonId, variant) in reader.ReadAllAsync(ct))
        {
            var key = JobKey(lessonId, variant);
            if (!_jobs.TryGetValue(key, out var job) || job.State != JobState.Queued) continue;
            var lesson = library.GetLesson(lessonId);
            if (lesson is null) { _jobs.TryRemove(key, out _); continue; }
            try
            {
                await RunAsync(lesson, variant, ct);
                Update(lesson, variant, JobState.Done, 100, null);
                TrimCache();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Preparing {Lesson} ({Variant}) failed", lesson.RelativePath, variant);
                Update(lesson, variant, JobState.Failed, 0, ex.Message);
            }
        }
    }

    private void Update(Lesson lesson, PlaybackVariant v, JobState state, int percent, string? error)
    {
        _jobs[JobKey(lesson.Id, v)] = new PrepareJob(lesson.Id, lesson.Title, v, state, percent, error, DateTime.UtcNow);
        Changed?.Invoke();
    }

    private async Task RunAsync(Lesson lesson, PlaybackVariant variant, CancellationToken ct)
    {
        var info = await probe.ProbeAsync(lesson.FullPath) ?? throw new InvalidOperationException("Could not read the video file.");
        var hw = (await settings.GetAsync<ServerSettings>()).HardwareAcceleration;
        var vaapiDevice = (await settings.GetAsync<ServerSettings>()).VaapiDevice;
        Update(lesson, variant, JobState.Running, 0, null);

        var target = PreparedPath(lesson, variant);
        var part = target + ".part.mp4";
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };

        if (variant == PlaybackVariant.Audio)
        {
            // Listening mode: audio only (AAC in M4A), a fraction of the video's size and playable with the screen off.
            if (info.AudioCodec is null) throw new InvalidOperationException("This video has no sound track.");
            args.AddRange(["-i", lesson.FullPath, "-map", "0:a:0", "-vn", "-sn", "-dn", "-map_metadata", "0", "-map_chapters", "0"]);
            args.AddRange(info.AudioCodec == "aac" ? ["-c:a", "copy"] : ["-c:a", "aac", "-b:a", "96k", "-ac", "2"]);
        }
        else
        {
        if (variant == PlaybackVariant.H264 && hw == "vaapi") args.AddRange(["-vaapi_device", vaapiDevice]);
        args.AddRange(["-i", lesson.FullPath, "-map", "0:v:0", "-map", "0:a:0?", "-map_metadata", "0", "-map_chapters", "0", "-sn", "-dn"]);

        if (variant == PlaybackVariant.Remux)
        {
            args.AddRange(["-c:v", "copy"]);
            if (info.VideoCodec == "hevc") args.AddRange(["-tag:v", "hvc1"]); // Safari needs the hvc1 tag
        }
        else
        {
            // Never upscale; cap at 1080p, which is plenty for lectures and screencasts.
            const string scale = "scale='min(1920,iw)':-2";
            switch (hw)
            {
                case "videotoolbox":
                    args.AddRange(["-vf", $"{scale},format=yuv420p", "-c:v", "h264_videotoolbox", "-b:v", "5M", "-maxrate", "8M", "-allow_sw", "1"]);
                    break;
                case "nvenc":
                    args.AddRange(["-vf", $"{scale},format=yuv420p", "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "23", "-b:v", "0"]);
                    break;
                case "qsv":
                    args.AddRange(["-vf", $"{scale},format=nv12", "-c:v", "h264_qsv", "-global_quality", "23"]);
                    break;
                case "v4l2m2m":
                    // Raspberry Pi 4's encoder: bitrate-controlled, up to 1080p; needs /dev/video11 in the container.
                    args.AddRange(["-vf", $"{scale},format=yuv420p", "-c:v", "h264_v4l2m2m", "-b:v", "5M", "-maxrate", "8M", "-bufsize", "10M"]);
                    break;
                case "vaapi":
                    args.AddRange(["-vf", "scale='min(1920,iw)':-2,format=nv12,hwupload", "-c:v", "h264_vaapi", "-qp", "23"]);
                    break;
                default:
                    args.AddRange(["-vf", $"{scale},format=yuv420p", "-c:v", "libx264", "-preset", "veryfast", "-crf", "22"]);
                    break;
            }
            args.AddRange(["-profile:v", "high"]);
        }

        if (info.AudioCodec is "aac" or "mp3" && variant == PlaybackVariant.Remux) args.AddRange(["-c:a", "copy"]);
        else args.AddRange(["-c:a", "aac", "-b:a", "160k", "-ac", "2"]);
        }
        args.AddRange(["-movflags", "+faststart", "-f", "mp4", part]);

        var duration = info.Duration ?? lesson.DurationSeconds ?? 0;
        var lastPercent = -1;
        var (code, err) = await MediaTools.RunAsync(tools.Ffmpeg!, args, TimeSpan.FromHours(6), line =>
        {
            // -progress prints key=value lines; out_time_us is microseconds of output written so far.
            if (duration <= 0 || !line.StartsWith("out_time_us=")) return;
            if (!long.TryParse(line.AsSpan(12), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)) return;
            var pct = (int)Math.Clamp(us / 1e6 / duration * 100, 0, 99);
            if (pct != lastPercent) { lastPercent = pct; Update(lesson, variant, JobState.Running, pct, null); }
        }, ct);

        if (code != 0 || !File.Exists(part))
        {
            try { File.Delete(part); } catch { /* ignore */ }
            var reason = err.Trim().Split('\n').LastOrDefault() ?? $"ffmpeg exited with {code}";
            if (hw != "none" && variant == PlaybackVariant.H264) reason += $" (hardware acceleration: {hw} — try 'none' in Settings if this keeps failing)";
            throw new InvalidOperationException(reason);
        }
        File.Move(part, target, overwrite: true);
        logger.LogInformation("Prepared {Lesson} as {Variant} ({Size} MB)", lesson.RelativePath, variant, new FileInfo(target).Length / 1024 / 1024);
    }

    private void TrimCache()
    {
        var limit = (long)Math.Max(1, settings.Current<ServerSettings>().TranscodeCacheGb) * 1024 * 1024 * 1024;
        var files = new DirectoryInfo(tools.TranscodeDir).GetFiles("*.mp4").Where(f => !f.Name.EndsWith(".part.mp4"))
            .OrderBy(f => f.LastAccessTimeUtc).ToList();
        var total = files.Sum(f => f.Length);
        foreach (var f in files)
        {
            if (total <= limit) break;
            try
            {
                total -= f.Length;
                f.Delete();
                logger.LogInformation("Removed {File} from the conversion cache", f.Name);
            }
            catch { /* in use */ }
        }
    }
}
