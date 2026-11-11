using System.Security.Cryptography;
using System.Text;
using Lyceum.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Lyceum.Services;

public sealed record GuestLinkInfo(int Id, string Label, string CoursePath, string CreatedBy, int CreatedByUserId, DateTime CreatedAt, DateTime ExpiresAt, DateTime? LastUsedAt, int Visits)
{
    public bool Expired => ExpiresAt <= DateTime.UtcNow;
}

/// <summary>
/// Links that let someone without an account watch a single course until a date. Each link signs into its
/// own hidden guest account (so progress and notes work), which is deleted with the link.
/// </summary>
public sealed class GuestService(IDbContextFactory<ProgressDb> dbFactory, UserService users, ILogger<GuestService> logger)
{
    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Creates a link and returns its secret token (only shown once).</summary>
    public async Task<string> CreateAsync(int creatorId, Course course, string label, TimeSpan validFor)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        await using var db = await dbFactory.CreateDbContextAsync();
        db.GuestLinks.Add(new GuestLink
        {
            TokenHash = Hash(token), CoursePath = course.FolderName,
            Label = string.IsNullOrWhiteSpace(label) ? "Guest" : label.Trim()[..Math.Min(label.Trim().Length, 60)],
            CreatedByUserId = creatorId, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.Add(validFor),
        });
        await db.SaveChangesAsync();
        logger.LogInformation("User {User} created a guest link for {Course} valid until {Expires}", creatorId, course.FolderName, DateTime.UtcNow.Add(validFor));
        return token;
    }

    /// <summary>Links for one course (or all, when <paramref name="coursePath"/> is null); non-admins only see their own.</summary>
    public async Task<List<GuestLinkInfo>> ListAsync(int viewerId, bool isAdmin, string? coursePath = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var names = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName ?? u.Username);
        return (await db.GuestLinks.AsNoTracking()
                .Where(g => (isAdmin || g.CreatedByUserId == viewerId) && (coursePath == null || g.CoursePath == coursePath))
                .OrderByDescending(g => g.CreatedAt).ToListAsync())
            .Select(g => new GuestLinkInfo(g.Id, g.Label, g.CoursePath, names.GetValueOrDefault(g.CreatedByUserId, "?"), g.CreatedByUserId,
                g.CreatedAt, g.ExpiresAt, g.LastUsedAt, g.Visits))
            .ToList();
    }

    /// <summary>Deletes the link, its guest account and everything the guest saved.</summary>
    public async Task<bool> RevokeAsync(int linkId, int viewerId, bool isAdmin)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var link = await db.GuestLinks.FirstOrDefaultAsync(g => g.Id == linkId && (isAdmin || g.CreatedByUserId == viewerId));
        if (link is null) return false;
        if (link.GuestUserId is { } guestId) await users.DeleteAsync(guestId, allowGuest: true);
        db.GuestLinks.Remove(link);
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>Validates a token and returns the guest account to sign in as (created on first use).</summary>
    // One redemption at a time: two first visits at once (a chat app's link preview plus the real click)
    // would otherwise each create a guest account, and only one would ever be cleaned up.
    private readonly SemaphoreSlim _redeemGate = new(1, 1);

    public async Task<(AppUser? User, GuestLink? Link)> RedeemAsync(string token)
    {
        await _redeemGate.WaitAsync();
        try { return await RedeemCoreAsync(token); }
        finally { _redeemGate.Release(); }
    }

    private async Task<(AppUser? User, GuestLink? Link)> RedeemCoreAsync(string token)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var link = await db.GuestLinks.FirstOrDefaultAsync(g => g.TokenHash == Hash(token));
        if (link is null || link.ExpiresAt <= DateTime.UtcNow) return (null, link);

        AppUser? guest = link.GuestUserId is { } id ? await db.Users.FindAsync(id) : null;
        if (guest is null)
        {
            guest = new AppUser
            {
                Username = $"guest-{link.Id}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}",
                DisplayName = link.Label, IsGuest = true, ShareActivity = false, CreatedAt = DateTime.UtcNow,
                PasswordHash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),
            };
            db.Users.Add(guest);
            await db.SaveChangesAsync();
            link.GuestUserId = guest.Id;
        }
        guest.GuestCourse = link.CoursePath;
        guest.GuestExpiresAt = link.ExpiresAt;
        guest.LastLoginAt = DateTime.UtcNow;
        link.LastUsedAt = DateTime.UtcNow;
        link.Visits++;
        await db.SaveChangesAsync();
        users.Invalidate(guest.Id);
        return (guest, link);
    }

    /// <summary>Removes guest accounts whose links expired more than a week ago.</summary>
    public async Task CleanupAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var cutoff = DateTime.UtcNow.AddDays(-7);
        foreach (var link in await db.GuestLinks.Where(g => g.ExpiresAt < cutoff).ToListAsync())
        {
            if (link.GuestUserId is { } id) await users.DeleteAsync(id, allowGuest: true);
            db.GuestLinks.Remove(link);
        }
        await db.SaveChangesAsync();
    }
}
