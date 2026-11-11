using System.Collections.Concurrent;
using System.Security.Claims;
using CourseHub.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CourseHub.Services;

public sealed record UserInfo(int Id, string Username, bool IsAdmin, DateTime CreatedAt, DateTime? LastLoginAt,
    string? DisplayName, string? LibraryAccess, bool ShareActivity)
{
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;

    /// <summary>Library ids this user may open; null means all.</summary>
    public IReadOnlySet<int>? AllowedLibraries => IsAdmin || LibraryAccess is null
        ? null
        : LibraryAccess.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
}

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
            .Select(u => new UserInfo(u.Id, u.Username, u.IsAdmin, u.CreatedAt, u.LastLoginAt, u.DisplayName, u.LibraryAccess, u.ShareActivity)).ToListAsync();
    }

    private readonly ConcurrentDictionary<int, UserInfo> _info = new();

    /// <summary>Cached profile used for access checks on every request.</summary>
    public async Task<UserInfo?> GetAsync(int userId)
    {
        if (_info.TryGetValue(userId, out var cached)) return cached;
        await using var db = await dbFactory.CreateDbContextAsync();
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId);
        if (u is null) return null;
        var info = new UserInfo(u.Id, u.Username, u.IsAdmin, u.CreatedAt, u.LastLoginAt, u.DisplayName, u.LibraryAccess, u.ShareActivity);
        _info[userId] = info;
        return info;
    }

    public async Task<IReadOnlySet<int>?> AllowedLibrariesAsync(int userId) =>
        await GetAsync(userId) is { } u ? u.AllowedLibraries : new HashSet<int>(); // unknown user: nothing

    /// <summary>null = every library.</summary>
    public async Task SetLibraryAccessAsync(int userId, IEnumerable<int>? libraryIds)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(x => x.SetProperty(u => u.LibraryAccess,
            libraryIds is null ? null : string.Join(',', libraryIds.Distinct().Order())));
        _info.TryRemove(userId, out _);
    }

    public async Task UpdateProfileAsync(int userId, string? displayName, bool shareActivity)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var name = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim()[..Math.Min(displayName.Trim().Length, 80)];
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(x => x
            .SetProperty(u => u.DisplayName, name)
            .SetProperty(u => u.ShareActivity, shareActivity));
        _info.TryRemove(userId, out _);
    }

    /// <summary>Signs a user in from an external identity provider, linking or creating the local account.</summary>
    public async Task<(AppUser? User, string? Error)> ExternalSignInAsync(
        string provider, string subject, string? preferredUsername, string? displayName, bool isAdmin, bool autoCreate, bool matchByUsername, bool syncAdmin)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var link = await db.ExternalLogins.FindAsync(provider, subject);
        AppUser? user = link is null ? null : await db.Users.FindAsync(link.UserId);

        var wanted = string.Concat((preferredUsername ?? displayName ?? "user").Where(c => !char.IsWhiteSpace(c)));
        if (wanted.Length == 0) wanted = "user";
        if (wanted.Length > 64) wanted = wanted[..64];

        if (user is null && matchByUsername)
            user = await db.Users.FirstOrDefaultAsync(u => u.Username == wanted);

        if (user is null)
        {
            if (!autoCreate) return (null, "Your account isn't set up in CourseHub yet. Ask an admin to create it.");
            var name = wanted;
            for (var i = 2; await db.Users.AnyAsync(u => u.Username == name); i++) name = $"{wanted}{i}";
            user = new AppUser
            {
                Username = name, DisplayName = displayName, CreatedAt = DateTime.UtcNow,
                // First account ever is always admin, like the setup page.
                IsAdmin = isAdmin || !await db.Users.AnyAsync(),
                PasswordHash = Hasher.HashPassword(new AppUser(), Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            _count = null;
            logger.LogInformation("Created account {User} from {Provider} sign-in", name, provider);
        }

        if (link is null)
            db.ExternalLogins.Add(new ExternalLogin { Provider = provider, Subject = subject, UserId = user.Id, CreatedAt = DateTime.UtcNow });
        if (syncAdmin && user.IsAdmin != isAdmin && !(user.IsAdmin && !isAdmin && await db.Users.CountAsync(u => u.IsAdmin) == 1))
        {
            user.IsAdmin = isAdmin;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
        }
        user.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        _stamps.TryRemove(user.Id, out _);
        _info.TryRemove(user.Id, out _);
        return (user, null);
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
        _info.TryRemove(userId, out _);
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
        await db.ApiKeys.Where(k => k.UserId == userId).ExecuteDeleteAsync();
        await db.ExternalLogins.Where(k => k.UserId == userId).ExecuteDeleteAsync();
        await db.Activity.Where(a => a.UserId == userId).ExecuteDeleteAsync();
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync();
        await tx.CommitAsync();
        _stamps.TryRemove(userId, out _);
        _info.TryRemove(userId, out _);
        _count = null;
        return null;
    }

    public static ClaimsPrincipal PrincipalFor(UserInfo user, string stamp, string scheme) => new(new ClaimsIdentity(
    [
        new Claim(Claims.UserId, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(Claims.Stamp, stamp),
        new Claim(Claims.Admin, user.IsAdmin ? "true" : "false"),
    ], scheme));
}
