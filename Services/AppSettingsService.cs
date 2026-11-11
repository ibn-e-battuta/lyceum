using System.Collections.Concurrent;
using System.Text.Json;
using CourseHub.Data;
using Microsoft.EntityFrameworkCore;

namespace CourseHub.Services;

/// <summary>Server-wide options admins change in Settings.</summary>
public sealed record ServerSettings
{
    /// <summary>Address people use to reach CourseHub, e.g. https://courses.example.com. Used in notification links.</summary>
    public string? PublicUrl { get; init; }
    public bool AutoTranscribeNewVideos { get; init; }
    /// <summary>Convert videos the browser can't play in the background as soon as they're found.</summary>
    public bool AutoPrepareVideos { get; init; }
    /// <summary>none | videotoolbox | nvenc | vaapi | qsv</summary>
    public string HardwareAcceleration { get; init; } = "none";
    public string VaapiDevice { get; init; } = "/dev/dri/renderD128";
    /// <summary>Converted videos are kept up to this size; the least recently watched are removed first.</summary>
    public int TranscodeCacheGb { get; init; } = 50;
    /// <summary>Spoken language for Whisper ("auto" to detect). Needs a multilingual model for anything but English.</summary>
    public string? WhisperLanguage { get; init; }
    /// <summary>Whisper model file name in storage/models, e.g. ggml-base.en.bin or ggml-small.bin.</summary>
    public string? WhisperModel { get; init; }
    /// <summary>Also create English captions for non-English lessons (Whisper's built-in translation).</summary>
    public bool TranslateToEnglish { get; init; }
}

/// <summary>An OpenAI-compatible chat API: Ollama (http://host:11434/v1), LM Studio, llama.cpp, vLLM, OpenAI…</summary>
public sealed record AiSettings
{
    public bool Enabled { get; init; }
    public string BaseUrl { get; init; } = "http://localhost:11434/v1";
    public string? ApiKey { get; init; }
    public string Model { get; init; } = "llama3.2";
    /// <summary>Embedding model for "ask this course" search by meaning, e.g. nomic-embed-text. Empty = keywords only.</summary>
    public string? EmbeddingModel { get; init; }
}

public sealed record NotificationTarget
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>webhook | ntfy</summary>
    public string Type { get; init; } = "webhook";
    /// <summary>Webhook URL, or the ntfy topic URL such as https://ntfy.sh/my-courses.</summary>
    public string Url { get; init; } = "";
    /// <summary>Optional bearer token (ntfy access token or webhook secret).</summary>
    public string? Token { get; init; }
    public bool OnCourseAdded { get; init; } = true;
    public bool OnCourseRemoved { get; init; }
    public bool OnTranscriptsDone { get; init; } = true;
    public bool OnCourseCompleted { get; init; }
}

public sealed record NotificationSettings
{
    public List<NotificationTarget> Targets { get; init; } = [];
}

/// <summary>Stores settings records as JSON rows. Reads are cached in memory.</summary>
public sealed class AppSettingsService(IDbContextFactory<ProgressDb> dbFactory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, object> _cache = new();

    public event Action? Changed;

    public async Task<T> GetAsync<T>() where T : new()
    {
        var key = typeof(T).Name;
        if (_cache.TryGetValue(key, out var cached)) return (T)cached;
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.Settings.FindAsync(key);
        T value;
        try
        {
            value = row is null ? new T() : JsonSerializer.Deserialize<T>(row.Value, Json) ?? new T();
        }
        catch (JsonException)
        {
            value = new T();
        }
        _cache[key] = value!;
        return value;
    }

    /// <summary>Cached value, for hot paths that can't await (falls back to defaults before the first load).</summary>
    public T Current<T>() where T : new() =>
        _cache.TryGetValue(typeof(T).Name, out var v) ? (T)v : Task.Run(GetAsync<T>).GetAwaiter().GetResult();

    public async Task SaveAsync<T>(T value) where T : notnull
    {
        var key = typeof(T).Name;
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.Settings.FindAsync(key);
        if (row is null) db.Settings.Add(row = new AppSetting { Key = key });
        row.Value = JsonSerializer.Serialize(value, Json);
        await db.SaveChangesAsync();
        _cache[key] = value;
        Changed?.Invoke();
    }
}
