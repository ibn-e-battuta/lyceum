using Lyceum.Data;
using Microsoft.EntityFrameworkCore;

namespace Lyceum.Services;

public sealed record DayTotal(DateOnly Day, double Seconds);

public sealed record CourseForecast(
    Course Course,
    CourseStats Stats,
    double WatchedSeconds,
    double RemainingSeconds,
    DateOnly? EstimatedFinish);

public sealed record LearningStats(
    IReadOnlyList<DayTotal> Last30Days,
    double TotalSeconds,
    double Last7DaysSeconds,
    double DailyAverageSeconds,
    int CurrentStreak,
    int LongestStreak,
    int ActiveDays,
    IReadOnlyList<CourseForecast> Courses);

public sealed class StatsService(IDbContextFactory<ProgressDb> dbFactory, CourseLibrary library)
{
    /// <summary>A day counts towards a streak once you've watched at least a minute.</summary>
    private const double StreakMinimumSeconds = 60;

    public async Task<LearningStats> GetAsync(int userId, AccessScope? allowedLibraries = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var days = await db.WatchDays.AsNoTracking().Where(d => d.UserId == userId).ToListAsync();
        var progress = await db.Progress.AsNoTracking().Where(p => p.UserId == userId).ToListAsync();

        var perDay = days
            .GroupBy(d => DateOnly.ParseExact(d.Day, "yyyy-MM-dd"))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Seconds));
        var today = DateOnly.FromDateTime(DateTime.Now);

        var last30 = Enumerable.Range(0, 30)
            .Select(i => today.AddDays(i - 29))
            .Select(d => new DayTotal(d, perDay.GetValueOrDefault(d)))
            .ToList();

        var active = perDay.Where(kv => kv.Value >= StreakMinimumSeconds).Select(kv => kv.Key).ToHashSet();

        // Current streak: today counts if active; otherwise the streak is still alive if yesterday was.
        var current = 0;
        var cursor = active.Contains(today) ? today : today.AddDays(-1);
        while (active.Contains(cursor)) { current++; cursor = cursor.AddDays(-1); }

        var longest = 0;
        foreach (var d in active)
        {
            if (active.Contains(d.AddDays(-1))) continue; // not the start of a run
            var len = 0;
            while (active.Contains(d.AddDays(len))) len++;
            longest = Math.Max(longest, len);
        }

        // Pace = average over the last 14 days, used to forecast finish dates.
        var last14 = Enumerable.Range(0, 14).Sum(i => perDay.GetValueOrDefault(today.AddDays(-i)));
        var dailyAverage = last14 / 14;

        var progressByCourse = progress.GroupBy(p => p.CoursePath).ToDictionary(g => g.Key, g => g.ToDictionary(p => p.LessonPath));
        var watchedByCourse = days.GroupBy(d => d.CoursePath).ToDictionary(g => g.Key, g => g.Sum(x => x.Seconds));

        var forecasts = new List<CourseForecast>();
        foreach (var course in library.CoursesFor(allowedLibraries))
        {
            var p = progressByCourse.GetValueOrDefault(course.FolderName) ?? [];
            var stats = ProgressService.Stats(course, p);
            var remaining = course.AllLessons.Sum(l =>
            {
                if (l.Kind != LessonKind.Video || l.DurationSeconds is null) return 0;
                var lp = p.GetValueOrDefault(l.RelativePath);
                if (lp?.Completed == true) return 0;
                return Math.Max(0, l.DurationSeconds.Value - (lp?.PositionSeconds ?? 0));
            });
            DateOnly? finish = null;
            // Need a real pace (5+ min/day over two weeks) before a forecast means anything.
            if (stats.Started && remaining > 0 && dailyAverage >= 300)
                finish = today.AddDays((int)Math.Ceiling(remaining / dailyAverage));
            forecasts.Add(new CourseForecast(course, stats, watchedByCourse.GetValueOrDefault(course.FolderName), remaining, finish));
        }

        return new LearningStats(
            last30,
            perDay.Values.Sum(),
            Enumerable.Range(0, 7).Sum(i => perDay.GetValueOrDefault(today.AddDays(-i))),
            dailyAverage,
            current,
            longest,
            active.Count,
            forecasts.OrderByDescending(f => f.Stats.LastWatched ?? DateTime.MinValue).ToList());
    }
}
