using System.Collections.Concurrent;
using Lyceum.Data;
using Microsoft.EntityFrameworkCore;

namespace Lyceum.Services;

/// <summary>Today's watch time per user, cached so it can be checked on every video request (kids' limits).</summary>
public sealed class WatchTimeService(IDbContextFactory<ProgressDb> dbFactory)
{
    private readonly ConcurrentDictionary<int, (string Day, double Seconds, DateTime LoadedAt)> _today = new();

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    public async Task<double> TodaySecondsAsync(int userId)
    {
        var day = Today;
        if (_today.TryGetValue(userId, out var c) && c.Day == day && DateTime.UtcNow - c.LoadedAt < TimeSpan.FromMinutes(2)) return c.Seconds;
        await using var db = await dbFactory.CreateDbContextAsync();
        var seconds = await db.WatchDays.Where(w => w.UserId == userId && w.Day == day).SumAsync(w => (double?)w.Seconds) ?? 0;
        _today[userId] = (day, seconds, DateTime.UtcNow);
        return seconds;
    }

    /// <summary>Called when progress is saved, so limits react immediately without a DB round-trip.</summary>
    public void Add(int userId, double seconds)
    {
        var day = Today;
        _today.AddOrUpdate(userId, _ => (day, seconds, DateTime.MinValue), (_, c) => c.Day == day ? (day, c.Seconds + seconds, c.LoadedAt) : (day, seconds, DateTime.MinValue));
    }

    /// <summary>Seconds left today for a kid with a daily limit; null when there's no limit.</summary>
    public async Task<double?> RemainingAsync(UserInfo user)
    {
        if (!user.IsKid || user.DailyLimitMinutes is not { } limit) return null;
        return Math.Max(0, limit * 60 - await TodaySecondsAsync(user.Id));
    }
}
