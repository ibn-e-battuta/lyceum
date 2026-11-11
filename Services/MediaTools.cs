using System.Diagnostics;
using System.Collections.Concurrent;

namespace CourseHub.Services;

public sealed class MediaToolsOptions
{
    public string? FfmpegPath { get; set; }
    public string? WhisperPath { get; set; }
    /// <summary>Where thumbnails, transcripts and the Whisper model are kept. Relative to the app folder.</summary>
    public string DataFolder { get; set; } = "storage";
    public string WhisperModel { get; set; } = "ggml-base.en.bin";
    public string WhisperModelUrl { get; set; } = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";
    public string WhisperLanguage { get; set; } = "en";
}

/// <summary>Finds ffmpeg / whisper-cli and makes lesson thumbnails.</summary>
public sealed class MediaTools
{
    private static readonly string[] SearchDirs = ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"];
    private readonly MediaToolsOptions _options;
    private readonly ILogger<MediaTools> _logger;
    private readonly SemaphoreSlim _thumbGate = new(3, 3);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inFlight = new();

    public MediaTools(MediaToolsOptions options, IWebHostEnvironment env, ILogger<MediaTools> logger)
    {
        _options = options;
        _logger = logger;
        DataDir = Path.GetFullPath(Path.Combine(env.ContentRootPath, options.DataFolder));
        ThumbDir = Directory.CreateDirectory(Path.Combine(DataDir, "thumbs")).FullName;
        TranscriptDir = Directory.CreateDirectory(Path.Combine(DataDir, "transcripts")).FullName;
        ModelDir = Directory.CreateDirectory(Path.Combine(DataDir, "models")).FullName;
        Ffmpeg = Find(options.FfmpegPath, "ffmpeg");
        Ffprobe = Ffmpeg is null ? null : Path.Combine(Path.GetDirectoryName(Ffmpeg)!, "ffprobe") is var fp && File.Exists(fp) ? fp : Find(null, "ffprobe");
        TranscodeDir = Directory.CreateDirectory(Path.Combine(DataDir, "transcodes")).FullName;
        Whisper = Find(options.WhisperPath, "whisper-cli") ?? Find(null, "whisper-cpp");
        logger.LogInformation("ffmpeg: {Ffmpeg}; whisper: {Whisper}", Ffmpeg ?? "not found", Whisper ?? "not found");
        _ = Task.Run(EncodersAsync); // ready long before anyone opens Settings
    }

    private Task<IReadOnlySet<string>>? _encoders;

    /// <summary>
    /// Video encoders this ffmpeg build has (h264_vaapi, h264_v4l2m2m…), read once from `ffmpeg -encoders`.
    /// Started at startup in the background; pages await <see cref="EncodersAsync"/> instead of blocking a render.
    /// </summary>
    public Task<IReadOnlySet<string>> EncodersAsync() => _encoders ??= DetectEncodersAsync();

    /// <summary>The detected encoders, or an empty set while detection is still running.</summary>
    public IReadOnlySet<string> Encoders => EncodersAsync() is { IsCompletedSuccessfully: true } t ? t.Result : new HashSet<string>();

    private async Task<IReadOnlySet<string>> DetectEncodersAsync()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Ffmpeg is null) return found;
        try
        {
            var lines = new List<string>();
            await RunAsync(Ffmpeg, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(15), lines.Add).ConfigureAwait(false);
            foreach (var l in lines)
            {
                var parts = l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].StartsWith('V')) found.Add(parts[1]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list ffmpeg encoders");
        }
        return found;
    }

    /// <summary>Hardware-acceleration choices (Settings value → ffmpeg encoder).</summary>
    public static readonly (string Value, string Encoder, string Label)[] HardwareOptions =
    [
        ("none", "libx264", "Software (libx264) — works everywhere"),
        ("vaapi", "h264_vaapi", "VAAPI — Intel / AMD (Linux, /dev/dri)"),
        ("qsv", "h264_qsv", "Intel Quick Sync (QSV)"),
        ("nvenc", "h264_nvenc", "NVIDIA NVENC"),
        ("v4l2m2m", "h264_v4l2m2m", "Raspberry Pi 4 hardware encoder (V4L2, /dev/video11)"),
        ("videotoolbox", "h264_videotoolbox", "Apple VideoToolbox (macOS)"),
    ];

    public bool HasEncoder(string value) =>
        HardwareOptions.FirstOrDefault(o => o.Value == value) is { Encoder: not null } o && Encoders.Contains(o.Encoder);

    public string DataDir { get; }
    public string ThumbDir { get; }
    public string TranscriptDir { get; }
    public string ModelDir { get; }
    public string? Ffmpeg { get; }
    public string? Ffprobe { get; }
    public string TranscodeDir { get; }
    public string? Whisper { get; }
    /// <summary>Model chosen in Settings, else the configured default.</summary>
    public string? ModelOverride { get; set; }
    public string ModelName => string.IsNullOrWhiteSpace(ModelOverride) ? _options.WhisperModel : ModelOverride;
    public string ModelPath => Path.Combine(ModelDir, ModelName);
    public string ModelUrlFor(string model) => _options.WhisperModelUrl.Replace(_options.WhisperModel, model);
    /// <summary>English-only models (*.en.bin) can't detect other languages or translate.</summary>
    public bool ModelIsMultilingual => !ModelName.Contains(".en.", StringComparison.OrdinalIgnoreCase);
    public string ModelUrl => ModelUrlFor(ModelName);
    public string? LanguageOverride { get; set; }
    public string Language => string.IsNullOrWhiteSpace(LanguageOverride) ? _options.WhisperLanguage : LanguageOverride;
    public bool ModelPresent => File.Exists(ModelPath);
    public bool CanTranscribe => Ffmpeg is not null && Whisper is not null && ModelPresent;

    private static string? Find(string? configured, string name)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var path = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        return path.Concat(SearchDirs).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
    }

    /// <summary>Returns the cached thumbnail for a video, creating it with ffmpeg on first request.</summary>
    public async Task<string?> ThumbnailAsync(Lesson lesson)
    {
        // Lazy makes sure concurrent requests for the same lesson share one ffmpeg run.
        var lazy = _inFlight.GetOrAdd(lesson.Id, _ => new Lazy<Task<string?>>(() => CreateThumbnail(lesson)));
        try
        {
            return await lazy.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Thumbnail failed for {Lesson}", lesson.RelativePath);
            return null;
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(lesson.Id, lazy));
        }
    }

    private async Task<string?> CreateThumbnail(Lesson lesson)
    {
        var file = Path.Combine(ThumbDir, lesson.Id + ".jpg");
        if (File.Exists(file) && File.GetLastWriteTimeUtc(file) >= File.GetLastWriteTimeUtc(lesson.FullPath)) return file;
        if (Ffmpeg is null || lesson.Kind != LessonKind.Video) return null;

        await _thumbGate.WaitAsync();
        try
        {
            // 10% in skips black intro frames; capped so long videos don't land mid-sentence somewhere random.
            var at = Math.Min((lesson.DurationSeconds ?? 60) * 0.1, 45);
            var tmp = Path.Combine(ThumbDir, $"{lesson.Id}.{Guid.NewGuid():N}.tmp.jpg");
            var (code, err) = await RunAsync(Ffmpeg, [
                "-hide_banner", "-loglevel", "error", "-y", "-ss", at.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                "-i", lesson.FullPath, "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "4", tmp,
            ], TimeSpan.FromSeconds(30));
            if (code != 0 || !File.Exists(tmp))
            {
                _logger.LogWarning("Thumbnail failed for {Lesson}: {Error}", lesson.RelativePath, err);
                return null;
            }
            File.Move(tmp, file, overwrite: true);
            return file;
        }
        finally
        {
            _thumbGate.Release();
        }
    }

    public static async Task<(int ExitCode, string StdErr)> RunAsync(
        string exe, IEnumerable<string> args, TimeSpan timeout, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        var err = new System.Text.StringBuilder();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (err.Length < 8000) err.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine?.Invoke(e.Data); };
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw;
        }
        return (p.ExitCode, err.ToString());
    }
}
