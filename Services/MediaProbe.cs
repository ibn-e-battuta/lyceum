using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lyceum.Services;

public sealed record Chapter(double Start, double End, string Title);

/// <summary>What's inside a video file, as far as browser playback is concerned.</summary>
public sealed record MediaInfo(
    string Container,
    string? VideoCodec,
    string? PixelFormat,
    int Width,
    int Height,
    string? AudioCodec,
    double? Duration,
    IReadOnlyList<Chapter> Chapters)
{
    public bool IsMp4Family => Container.Contains("mp4") || Container.Contains("mov");
    public bool IsWebm => Container.Contains("webm");
    public bool Is10Bit => PixelFormat?.Contains("10") == true || PixelFormat?.Contains("12") == true;

    /// <summary>Codec name in the form browsers' canPlayType() understands, or null if no browser plays it.</summary>
    public string? VideoCodecTag => VideoCodec switch
    {
        "h264" when !Is10Bit => "avc1.640028",
        "hevc" => "hvc1.1.6.L120.90",
        "vp9" => "vp09.00.40.08",
        "vp8" => "vp8",
        "av1" => "av01.0.08M.08",
        _ => null,
    };

    public string? AudioCodecTag => AudioCodec switch
    {
        "aac" => "mp4a.40.2",
        "mp3" => "mp4a.6B",
        "opus" => "opus",
        "vorbis" => "vorbis",
        "flac" => "flac",
        null => null,
        _ => null,
    };

    /// <summary>MIME type to test the original file with, or null when it can't be played directly.</summary>
    public string? DirectMime
    {
        get
        {
            if (VideoCodecTag is null || (AudioCodec is not null && AudioCodecTag is null)) return null;
            var codecs = AudioCodecTag is null ? VideoCodecTag : $"{VideoCodecTag}, {AudioCodecTag}";
            if (IsMp4Family) return $"video/mp4; codecs=\"{codecs}\"";
            if (IsWebm) return $"video/webm; codecs=\"{codecs}\"";
            return null; // mkv, avi, ts…: browsers can't be relied on
        }
    }

    /// <summary>MIME type of the file we'd get by copying the video into MP4 (audio becomes AAC if needed).</summary>
    public string? RemuxMime => VideoCodecTag is { } v && VideoCodec is "h264" or "hevc" or "av1" or "vp9"
        ? $"video/mp4; codecs=\"{v}, mp4a.40.2\""
        : null;
}

/// <summary>Reads codecs, chapters and duration with ffprobe. Results are cached on disk.</summary>
public sealed class MediaProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly MediaTools _tools;
    private readonly ILogger<MediaProbe> _logger;
    private readonly ConcurrentDictionary<string, MediaInfo?> _cache = new();
    private readonly string _cacheFile;
    private readonly SemaphoreSlim _gate = new(4, 4);
    private int _dirty;

    public MediaProbe(MediaTools tools, ILogger<MediaProbe> logger)
    {
        _tools = tools;
        _logger = logger;
        _cacheFile = Path.Combine(tools.DataDir, "probe-cache.json");
        try
        {
            if (File.Exists(_cacheFile))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, MediaInfo?>>(File.ReadAllText(_cacheFile), Json) ?? [])
                    _cache[k] = v;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Ignoring unreadable probe cache");
        }
    }

    public bool Available => _tools.Ffprobe is not null;

    private static string Key(string path)
    {
        var info = new FileInfo(path);
        return $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    public MediaInfo? Cached(string path) => _cache.TryGetValue(Key(path), out var v) ? v : null;

    /// <summary>Synchronous variant for the library scan (only used for files Mp4Duration can't read).</summary>
    public double? Duration(string path) => Available ? Task.Run(() => ProbeAsync(path)).GetAwaiter().GetResult()?.Duration : null;

    public async Task<MediaInfo?> ProbeAsync(string path)
    {
        if (!Available || !File.Exists(path)) return null;
        var key = Key(path);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        await _gate.WaitAsync();
        try
        {
            if (_cache.TryGetValue(key, out cached)) return cached;
            var output = new System.Text.StringBuilder();
            var (code, err) = await MediaTools.RunAsync(_tools.Ffprobe!,
                ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", path],
                TimeSpan.FromSeconds(30), line => output.AppendLine(line));
            MediaInfo? info = null;
            if (code == 0) info = Parse(output.ToString());
            else _logger.LogWarning("ffprobe failed for {Path}: {Error}", path, err.Trim());
            _cache[key] = info;
            ScheduleSave();
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffprobe failed for {Path}", path);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ScheduleSave()
    {
        if (Interlocked.Exchange(ref _dirty, 1) == 1) return;
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref _dirty, 0);
            try
            {
                var tmp = _cacheFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_cache.ToDictionary(kv => kv.Key, kv => kv.Value), Json));
                File.Move(tmp, _cacheFile, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save probe cache");
            }
        });
    }

    private static MediaInfo? Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<ProbeOutput>(json, Json);
        if (doc?.Format is null) return null;
        var video = doc.Streams?.FirstOrDefault(s => s.CodecType == "video" && s.Disposition?.AttachedPic != 1);
        var audio = doc.Streams?.FirstOrDefault(s => s.CodecType == "audio");
        var chapters = (doc.Chapters ?? [])
            .Select((c, i) => new Chapter(D(c.StartTime) ?? 0, D(c.EndTime) ?? 0,
                c.Tags?.GetValueOrDefault("title") is { Length: > 0 } t ? t : $"Chapter {i + 1}"))
            .Where(c => c.End > c.Start)
            .ToList();
        return new MediaInfo(doc.Format.FormatName ?? "", video?.CodecName, video?.PixFmt, video?.Width ?? 0, video?.Height ?? 0,
            audio?.CodecName, D(doc.Format.Duration), chapters);
    }

    private static double? D(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private sealed class ProbeOutput
    {
        public List<ProbeStream>? Streams { get; set; }
        public ProbeFormat? Format { get; set; }
        public List<ProbeChapter>? Chapters { get; set; }
    }

    private sealed class ProbeStream
    {
        [JsonPropertyName("codec_type")] public string? CodecType { get; set; }
        [JsonPropertyName("codec_name")] public string? CodecName { get; set; }
        [JsonPropertyName("pix_fmt")] public string? PixFmt { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public ProbeDisposition? Disposition { get; set; }
    }

    private sealed class ProbeDisposition
    {
        [JsonPropertyName("attached_pic")] public int AttachedPic { get; set; }
    }

    private sealed class ProbeFormat
    {
        [JsonPropertyName("format_name")] public string? FormatName { get; set; }
        public string? Duration { get; set; }
    }

    private sealed class ProbeChapter
    {
        [JsonPropertyName("start_time")] public string? StartTime { get; set; }
        [JsonPropertyName("end_time")] public string? EndTime { get; set; }
        public Dictionary<string, string>? Tags { get; set; }
    }
}
