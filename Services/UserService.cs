using System.Collections.Concurrent;
using System.Security.Claims;
using CourseHub.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CourseHub.Services;

public sealed record UserInfo(int Id, string Username, bool IsAdmin, DateTime CreatedAt, DateTime? LastLoginAt);

public static class Claims
{
    public const string UserId = ClaimTypes.NameIdentifier;
    public const string Stamp = "coursehub:stamp";
    public const string Admin = "coursehub:admin";

    public static int? GetUserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(UserId), out var id) ? id : null;

    public static bool IsAdmin(this ClaimsPrincipal user) => user.HasClaim(Admin, "true");
}

/// <summary>Accounts, passwords and sign-in checks.</summary>
public sealed class UserService(IDbContextFactory<ProgressDb> dbFactory, ILogger<UserService> logger)
{
    public const int MinPasswordLength = 8;

    private static readonly PasswordHasher<AppUser> Hasher = new();
    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword(new AppUser(), Guid.NewGuid().ToString()));
    // Small cache so validating the auth cookie on every request (including video range requests) doesn't hit the DB.
    private readonly ConcurrentDictionary<int, (string Stamp, bool IsAdmin)> _stamps = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTime WindowStart)> _failures = new();
    private int? _count;

    public async Task<int> CountAsync()
    {
        if (_count is { } c) return c;
        await using var db = await dbFactory.CreateDbContextAsync();
        return (_count = await db.Users.CountAsync()).Value;
    }

    public async Task<List<UserInfo>> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.AsNoTracking().OrderBy(u => u.Username)
            .Select(u => new UserInfo(u.Id, u.Username, u.IsAdmin, u.CreatedAt, u.LastLoginAt)).ToListAsync();
    }

    public static string? ValidateNew(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Trim().Length > 64) return "Choose a username (up to 64 characters).";
        if (username.Trim().Any(char.IsWhiteSpace)) return "Usernames can't contain spaces.";
        if (password.Length < MinPasswordLength) return $"Passwords need at least {MinPasswordLength} characters.";
        return null;
    }

    public async Task<(AppUser? User, string? Error)> CreateAsync(string username, string password, bool isAdmin)
    {
        if (ValidateNew(username, password) is { } err) return (null, err);
        await using var db = await dbFactory.CreateDbContextAsync();
        username = username.Trim();
        if (await db.Users.AnyAsync(u => u.Username == username)) return (null, "That username is taken.");
        var user = new AppUser { Username = username, IsAdmin = isAdmin, CreatedAt = DateTime.UtcNow };
        user.PasswordHash = Hasher.HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        _count = null;
        logger.LogInformation("Created {Role} account {User}", isAdmin ? "admin" : "user", username);
        return (user, null);
    }

    /// <summary>Creates the first admin from environment variables, for headless installs.</summary>
    public async Task BootstrapAdminAsync(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password) || await CountAsync() > 0) return;
        var (_, err) = await CreateAsync(username, password, isAdmin: true);
        if (err is not null) logger.LogError("Could not create the initial admin from environment variables: {Error}", err);
    }

    // ---------- sign in ----------

    /// <summary>Blocks a username/IP pair for 15 minutes after 8 wrong passwords.</summary>
    public bool IsLockedOut(string key) =>
        _failures.TryGetValue(key, out var f) && f.Failures >= 8 && DateTime.UtcNow - f.WindowStart < TimeSpan.FromMinutes(15);

    public async Task<AppUser?> VerifyAsync(string username, string password, string throttleKey)
    {
        if (IsLockedOut(throttleKey)) return null;
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username.Trim());
        var ok = false;
        if (user is not null)
        {
            var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
            ok = result != PasswordVerificationResult.Failed;
            if (result == PasswordVerificationResult.SuccessRehashNeeded) user.PasswordHash = Hasher.HashPassword(user, password);
        }
        else
        {
            // Spend the same time as a real check so usernames can't be discovered by timing.
            Hasher.VerifyHashedPassword(new AppUser(), DummyHash.Value, password);
        }

        if (!ok)
        {
            _failures.AddOrUpdate(throttleKey, (1, DateTime.UtcNow), (_, f) =>
                DateTime.UtcNow - f.WindowStart > TimeSpan.FromMinutes(15) ? (1, DateTime.UtcNow) : (f.Failures + 1, f.WindowStart));
            return null;
        }
        _failures.TryRemove(throttleKey, out _);
        user!.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return user;
    }

    public static ClaimsPrincipal Principal(AppUser user) => new(new ClaimsIdentity(
    [
        new Claim(Claims.UserId, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(Claims.Stamp, user.SecurityStamp),
        new Claim(Claims.Admin, user.IsAdmin ? "true" : "false"),
    ], CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>Rejects cookies for deleted users or after a password/role change.</summary>
    public async Task ValidatePrincipalAsync(CookieValidatePrincipalContext ctx)
    {
        var id = ctx.Principal?.GetUserId();
        var stamp = ctx.Principal?.FindFirstValue(Claims.Stamp);
        if (id is null || stamp is null) { ctx.RejectPrincipal(); return; }
        if (!_stamps.TryGetValue(id.Value, out var current))
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id);
            if (user is null) { ctx.RejectPrincipal(); return; }
            current = (user.SecurityStamp, user.IsAdmin);
            _stamps[id.Value] = current;
        }
        if (current.Stamp != stamp || ctx.Principal!.IsAdmin() != current.IsAdmin) ctx.RejectPrincipal();
    }

    // ---------- management ----------

    public async Task<string?> ChangePasswordAsync(int userId, string? currentPassword, string newPassword)
    {
        if (newPassword.Length < MinPasswordLength) return $"Passwords need at least {MinPasswordLength} characters.";
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(userId);
        if (user is null) return "User not found.";
        if (currentPassword is not null && Hasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
            return "Your current password is wrong.";
        user.PasswordHash = Hasher.HashPassword(user, newPassword);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();
        _stamps.TryRemove(userId, out _);
        return null;
    }

    public async Task<string?> SetAdminAsync(int userId, bool isAdmin)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(userId);
        if (user is null) return "User not found.";
        if (!isAdmin && user.IsAdmin && await db.Users.CountAsync(u => u.IsAdmin) == 1) return "There must be at least one admin.";
        user.IsAdmin = isAdmin;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync();
        _stamps.TryRemove(userId, out _);
        return null;
    }

    /// <summary>Deletes the account and everything it saved.</summary>
    public async Task<string?> DeleteAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(userId);
        if (user is null) return "User not found.";
        if (user.IsAdmin && await db.Users.CountAsync(u => u.IsAdmin) == 1) return "You can't delete the last admin.";
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Progress.Where(p => p.UserId == userId).ExecuteDeleteAsync();
        await db.Notes.Where(p => p.UserId == userId).ExecuteDeleteAsync();
        await db.WatchDays.Where(p => p.UserId == userId).ExecuteDeleteAsync();
        await db.CourseMeta.Where(p => p.UserId == userId).ExecuteDeleteAsync();
        var pathIds = await db.Paths.Where(p => p.UserId == userId).Select(p => p.Id).ToListAsync();
        await db.PathItems.Where(i => pathIds.Contains(i.LearningPathId)).ExecuteDeleteAsync();
        await db.Paths.Where(p => p.UserId == userId).ExecuteDeleteAsync();
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync();
        await tx.CommitAsync();
        _stamps.TryRemove(userId, out _);
        _count = null;
        return null;
    }
}
