using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Lyceum.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lyceum.Services;

public sealed record ApiKeyInfo(int Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>Personal API keys for scripts and apps. Only a SHA-256 hash is stored.</summary>
public sealed class ApiKeyService(IDbContextFactory<ProgressDb> dbFactory)
{
    public const string KeyPrefix = "ch_";
    private readonly ConcurrentDictionary<string, (int UserId, int KeyId, DateTime CheckedAt)> _cache = new();
    private readonly ConcurrentDictionary<int, DateTime> _lastTouch = new();

    private static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public async Task<List<ApiKeyInfo>> ListAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.ApiKeys.AsNoTracking().Where(k => k.UserId == userId).OrderByDescending(k => k.CreatedAt)
            .Select(k => new ApiKeyInfo(k.Id, k.Name, k.Prefix, k.CreatedAt, k.LastUsedAt)).ToListAsync();
    }

    /// <summary>Creates a key and returns it. This is the only time the full key is available.</summary>
    public async Task<string> CreateAsync(int userId, string name)
    {
        var key = KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(30)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        await using var db = await dbFactory.CreateDbContextAsync();
        db.ApiKeys.Add(new ApiKey
        {
            UserId = userId, Name = string.IsNullOrWhiteSpace(name) ? "API key" : name.Trim(), Prefix = key[..10],
            KeyHash = Hash(key), CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return key;
    }

    public async Task DeleteAsync(int userId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ApiKeys.Where(k => k.Id == id && k.UserId == userId).ExecuteDeleteAsync();
        _cache.Clear();
    }

    /// <summary>Returns the owning user id, or null for an unknown key.</summary>
    public async Task<int?> ValidateAsync(string key)
    {
        if (!key.StartsWith(KeyPrefix) || key.Length < 20) return null;
        var hash = Hash(key);
        if (_cache.TryGetValue(hash, out var c) && DateTime.UtcNow - c.CheckedAt < TimeSpan.FromMinutes(1))
        {
            await TouchAsync(c.KeyId);
            return c.UserId;
        }
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.KeyHash == hash);
        if (row is null) { _cache.TryRemove(hash, out _); return null; }
        _cache[hash] = (row.UserId, row.Id, DateTime.UtcNow);
        await TouchAsync(row.Id);
        return row.UserId;
    }

    private async Task TouchAsync(int keyId)
    {
        // Record "last used" at most every 5 minutes per key.
        if (_lastTouch.TryGetValue(keyId, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(5)) return;
        _lastTouch[keyId] = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ApiKeys.Where(k => k.Id == keyId).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, DateTime.UtcNow));
    }
}

/// <summary>Authenticates requests carrying an API key in Authorization: Bearer, X-Api-Key, or (media URLs only) ?api_key=.</summary>
public sealed class ApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyService keys,
    UserService users) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    public static string? KeyFrom(HttpRequest r)
    {
        var auth = r.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer " + ApiKeyService.KeyPrefix, StringComparison.Ordinal)) return auth[7..].Trim();
        if (r.Headers.TryGetValue("X-Api-Key", out var h) && h.ToString().Length > 0) return h.ToString();
        // <video src> can't send headers, so apps may put the key in the URL — but only for media files.
        if (r.Query.TryGetValue("api_key", out var q) && IsMediaPath(r.Path)) return q.ToString();
        return null;
    }

    private static bool IsMediaPath(PathString p) =>
        p.StartsWithSegments("/media") || p.StartsWithSegments("/subs") || p.StartsWithSegments("/thumb")
        || p.StartsWithSegments("/cover") || p.StartsWithSegments("/attachment");

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = KeyFrom(Request);
        if (key is null) return AuthenticateResult.NoResult();
        var userId = await keys.ValidateAsync(key);
        if (userId is null) return AuthenticateResult.Fail("Invalid API key.");
        var user = await users.GetAsync(userId.Value);
        if (user is null) return AuthenticateResult.Fail("The key's user no longer exists.");
        var principal = UserService.PrincipalFor(user, "", SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}

/// <summary>Who is watching what right now (for the admin dashboard).</summary>
public sealed class PresenceService
{
    private readonly ConcurrentDictionary<int, (Course Course, Lesson Lesson, DateTime At)> _now = new();

    public void Track(int userId, Course course, Lesson lesson) => _now[userId] = (course, lesson, DateTime.UtcNow);

    public IReadOnlyList<(int UserId, Course Course, Lesson Lesson, DateTime At)> Active(TimeSpan within) =>
        _now.Where(kv => DateTime.UtcNow - kv.Value.At < within).Select(kv => (kv.Key, kv.Value.Course, kv.Value.Lesson, kv.Value.At)).ToList();
}
