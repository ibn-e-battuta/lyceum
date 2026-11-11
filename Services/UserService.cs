using System.Collections.Concurrent;
using System.Security.Claims;
using Lyceum.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Lyceum.Services;

public sealed record UserInfo(int Id, string Username, bool IsAdmin, DateTime CreatedAt, DateTime? LastLoginAt,
    string? DisplayName, string? LibraryAccess, bool ShareActivity)
{
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;
    public bool TotpEnabled { get; init; }
    public bool IsKid { get; init; }
    public int? DailyLimitMinutes { get; init; }
    public int? DailyGoalMinutes { get; init; }
    public string? ReminderTime { get; init; }
    public string? ReminderUrl { get; init; }
    public bool HasReminderToken { get; init; }
    public bool IsGuest { get; init; }
    public string? GuestCourse { get; init; }
    public DateTime? GuestExpiresAt { get; init; }

    /// <summary>Library ids this user may open; null means all.</summary>
    public IReadOnlySet<int>? AllowedLibraries => IsAdmin || LibraryAccess is null
        ? null
        : LibraryAccess.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();

    /// <summary>Everything this user may see; null means everything.</summary>
    public AccessScope? Scope => IsGuest ? new AccessScope(null, GuestCourse ?? "\0")
        : AllowedLibraries is { } libs ? new AccessScope(libs, null) : null;

    public static UserInfo From(AppUser u) => new(u.Id, u.Username, u.IsAdmin, u.CreatedAt, u.LastLoginAt, u.DisplayName, u.LibraryAccess, u.ShareActivity)
    {
        TotpEnabled = u.TotpEnabled, IsKid = u.IsKid, DailyLimitMinutes = u.DailyLimitMinutes, DailyGoalMinutes = u.DailyGoalMinutes,
        ReminderTime = u.ReminderTime, ReminderUrl = u.ReminderUrl, HasReminderToken = !string.IsNullOrEmpty(u.ReminderToken),
        IsGuest = u.IsGuest, GuestCourse = u.GuestCourse, GuestExpiresAt = u.GuestExpiresAt,
    };
}

public static class Claims
{
    public const string UserId = ClaimTypes.NameIdentifier;
    public const string Stamp = "lyceum:stamp";
    public const string Admin = "lyceum:admin";
    public const string Guest = "lyceum:guest";

    public static int? GetUserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(UserId), out var id) ? id : null;

    public static bool IsAdmin(this ClaimsPrincipal user) => user.HasClaim(Admin, "true");
    public static bool IsGuest(this ClaimsPrincipal user) => user.HasClaim(Guest, "true");
}

/// <summary>Accounts, passwords and sign-in checks.</summary>
public sealed class UserService(IDbContextFactory<ProgressDb> dbFactory, ILogger<UserService> logger)
{
    public const int MinPasswordLength = 8;

    private static readonly PasswordHasher<AppUser> Hasher = new();
    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword(new AppUser(), Guid.NewGuid().ToString()));
    // Small cache so validating the auth cookie on every request (including video range requests) doesn't hit the DB.
    private readonly ConcurrentDictionary<int, (string Stamp, bool IsAdmin, DateTime? GuestExpires)> _stamps = new();
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
        var list = await db.Users.AsNoTracking().OrderBy(u => u.Username)
            .Where(u => !u.IsGuest).ToListAsync();
        return list.Select(UserInfo.From).ToList();
    }

    private readonly ConcurrentDictionary<int, UserInfo> _info = new();

    /// <summary>Cached profile used for access checks on every request.</summary>
    public async Task<UserInfo?> GetAsync(int userId)
    {
        if (_info.TryGetValue(userId, out var cached)) return cached;
        await using var db = await dbFactory.CreateDbContextAsync();
        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId);
        if (u is null) return null;
        var info = UserInfo.From(u);
        _info[userId] = info;
        return info;
    }

    public async Task<AccessScope?> ScopeAsync(int userId) =>
        await GetAsync(userId) is { } u ? u.Scope : AccessScope.Nothing; // unknown user: nothing

    /// <summary>Forget cached profile data after it was changed elsewhere.</summary>
    public void Invalidate(int userId)
    {
        _info.TryRemove(userId, out _);
        _stamps.TryRemove(userId, out _);
    }

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
            if (!autoCreate) return (null, "Your account isn't set up in Lyceum yet. Ask an admin to create it.");
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
        new Claim(Claims.Guest, user.IsGuest ? "true" : "false"),
    ], CookieAuthenticationDefaults.AuthenticationScheme));

    /// <summary>The user's current sign-in stamp, role and guest expiry (cached); null when the user no longer exists.</summary>
    private async Task<(string Stamp, bool IsAdmin, DateTime? GuestExpires)?> SessionAsync(int userId)
    {
        if (_stamps.TryGetValue(userId, out var current)) return current;
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return null;
        current = (user.SecurityStamp, user.IsAdmin, user.IsGuest ? user.GuestExpiresAt ?? DateTime.MinValue : null);
        _stamps[userId] = current;
        return current;
    }

    /// <summary>The stamp to embed in links that act for this user (cast links), so they end with the user's sessions.</summary>
    public async Task<string?> CurrentStampAsync(int userId) => (await SessionAsync(userId))?.Stamp;

    /// <summary>False after a password/role change, a sign-out everywhere, deletion, or a guest link's expiry.</summary>
    public async Task<bool> IsSessionValidAsync(int userId, string stamp, bool? isAdmin = null) =>
        await SessionAsync(userId) is { } s && s.Stamp == stamp && (isAdmin is null || isAdmin == s.IsAdmin)
        && !(s.GuestExpires is { } expires && expires <= DateTime.UtcNow);

    /// <summary>Rejects cookies for deleted users, after a password/role change, or once a guest link expired.</summary>
    public async Task ValidatePrincipalAsync(CookieValidatePrincipalContext ctx)
    {
        var id = ctx.Principal?.GetUserId();
        var stamp = ctx.Principal?.FindFirstValue(Claims.Stamp);
        if (id is null || stamp is null || !await IsSessionValidAsync(id.Value, stamp, ctx.Principal!.IsAdmin())) ctx.RejectPrincipal();
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
    public async Task<string?> DeleteAsync(int userId, bool allowGuest = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(userId);
        if (user is null) return "User not found.";
        if (user.IsGuest && !allowGuest) return "Revoke the guest link instead.";
        if (user.IsAdmin && await db.Users.CountAsync(u => u.IsAdmin) == 1) return "You can't delete the last admin.";
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Comments.Where(c => c.UserId == userId).ExecuteDeleteAsync();
        await db.ReviewCards.Where(c => c.UserId == userId).ExecuteDeleteAsync();
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

    public async Task<AppUser?> FindForSignInAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
    }

    // ---------- two-factor sign-in ----------

    /// <summary>Starts setup: stores a new (not yet active) secret and returns it for the QR code.</summary>
    public async Task<string> BeginTotpSetupAsync(int userId, TotpService totp)
    {
        var secret = totp.NewSecret();
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users.Where(u => u.Id == userId && !u.TotpEnabled)
            .ExecuteUpdateAsync(x => x.SetProperty(u => u.TotpSecret, totp.Protect(secret)));
        return secret;
    }

    /// <summary>Turns two-factor on after the first correct code. Returns the recovery codes, or null when the code is wrong.</summary>
    public async Task<List<string>?> ConfirmTotpAsync(int userId, string code, TotpService totp)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(userId);
        if (user?.TotpSecret is null || user.TotpEnabled) return null;
        var step = TotpService.Verify(totp.Unprotect(user.TotpSecret), code, 0);
        if (step is null) return null;
        var recovery = TotpService.NewRecoveryCodes();
        user.TotpEnabled = true;
        user.TotpLastStep = step.Value;
        user.RecoveryCodes = string.Join(';', recovery.Select(TotpService.HashRecovery));
        user.SecurityStamp = Guid.NewGuid().ToString("N"); // other sessions must sign in again with the code
        await db.SaveChangesAsync();
        Invalidate(userId);
        return recovery;
    }

    public async Task DisableTotpAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(x => x
            .SetProperty(u => u.TotpEnabled, false).SetProperty(u => u.TotpSecret, (string?)null)
            .SetProperty(u => u.RecoveryCodes, (string?)null).SetProperty(u => u.TotpLastStep, 0));
        Invalidate(userId);
    }

    /// <summary>Checks a 6-digit code or a recovery code (recovery codes work once).</summary>
    public async Task<bool> VerifySecondFactorAsync(int userId, string code, string throttleKey, TotpService totp)
    {
        if (IsLockedOut(throttleKey)) return false;
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null || !user.TotpEnabled || user.TotpSecret is null) return false;

        // Each code is consumed with one conditional UPDATE, so two requests racing with the same
        // code (or the same recovery code) can't both win: the second finds the row already changed.
        var ok = false;
        if (TotpService.Verify(totp.Unprotect(user.TotpSecret), code, user.TotpLastStep) is { } step)
        {
            ok = await db.Users.Where(u => u.Id == userId && u.TotpLastStep < step)
                .ExecuteUpdateAsync(x => x.SetProperty(u => u.TotpLastStep, step).SetProperty(u => u.LastLoginAt, DateTime.UtcNow)) == 1;
        }
        else if (user.RecoveryCodes is { Length: > 0 } stored)
        {
            var hashes = stored.Split(';').ToList();
            if (hashes.Remove(TotpService.HashRecovery(code)))
            {
                var remaining = string.Join(';', hashes);
                ok = await db.Users.Where(u => u.Id == userId && u.RecoveryCodes == stored)
                    .ExecuteUpdateAsync(x => x.SetProperty(u => u.RecoveryCodes, remaining).SetProperty(u => u.LastLoginAt, DateTime.UtcNow)) == 1;
                if (ok) logger.LogWarning("User {User} signed in with a recovery code ({Left} left)", user.Username, hashes.Count);
            }
        }
        if (!ok)
        {
            _failures.AddOrUpdate(throttleKey, (1, DateTime.UtcNow), (_, f) =>
                DateTime.UtcNow - f.WindowStart > TimeSpan.FromMinutes(15) ? (1, DateTime.UtcNow) : (f.Failures + 1, f.WindowStart));
            return false;
        }
        _failures.TryRemove(throttleKey, out _);
        return true;
    }

    public async Task<int> RecoveryCodesLeftAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var stored = await db.Users.Where(u => u.Id == userId).Select(u => u.RecoveryCodes).FirstOrDefaultAsync();
        return string.IsNullOrEmpty(stored) ? 0 : stored.Split(';').Length;
    }

    // ---------- kids, goals and reminders ----------

    public async Task SetKidAsync(int userId, bool isKid, int? dailyLimitMinutes)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users.Where(u => u.Id == userId && !u.IsAdmin).ExecuteUpdateAsync(x => x
            .SetProperty(u => u.IsKid, isKid)
            .SetProperty(u => u.DailyLimitMinutes, isKid && dailyLimitMinutes is > 0 ? dailyLimitMinutes : null));
        Invalidate(userId);
    }

    public async Task SetGoalAsync(int userId, int? goalMinutes, string? reminderTime, string? reminderUrl, string? reminderToken, bool keepToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var user = await db.Users.FindAsync(userId);
        if (user is null) return;
        user.DailyGoalMinutes = goalMinutes is > 0 ? Math.Min(goalMinutes.Value, 24 * 60) : null;
        user.ReminderTime = TimeOnly.TryParse(reminderTime, out var t) ? t.ToString("HH:mm") : null;
        user.ReminderUrl = string.IsNullOrWhiteSpace(reminderUrl) ? null : reminderUrl.Trim();
        if (!keepToken) user.ReminderToken = string.IsNullOrWhiteSpace(reminderToken) ? null : reminderToken.Trim();
        await db.SaveChangesAsync();
        Invalidate(userId);
    }

    public async Task MarkRemindedAsync(int userId, string day)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(x => x.SetProperty(u => u.LastReminderDay, day));
    }

    /// <summary>Users who asked for a reminder at this minute and haven't had one today.</summary>
    public async Task<List<AppUser>> DueForReminderAsync(string hhmm, string today)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Users.AsNoTracking()
            .Where(u => !u.IsGuest && u.ReminderTime == hhmm && u.ReminderUrl != null && u.LastReminderDay != today)
            .ToListAsync();
    }

    public static ClaimsPrincipal PrincipalFor(UserInfo user, string stamp, string scheme) => new(new ClaimsIdentity(
    [
        new Claim(Claims.UserId, user.Id.ToString()),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(Claims.Stamp, stamp),
        new Claim(Claims.Admin, user.IsAdmin ? "true" : "false"),
    ], scheme));
}
