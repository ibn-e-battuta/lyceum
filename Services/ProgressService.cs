using CourseHub.Data;
using Microsoft.EntityFrameworkCore;

namespace CourseHub.Services;

public sealed record CourseStats(int Completed, int Total, Lesson? ResumeLesson, DateTime? LastWatched)
{
    public double Percent => Total == 0 ? 0 : Math.Round(100.0 * Completed / Total);
    public bool Started => LastWatched is not null;
}

/// <summary>Per-user watch progress.</summary>
public sealed class ProgressService(IDbContextFactory<ProgressDb> dbFactory)
{
    /// <summary>A lesson counts as complete once 92% has been watched (skips end credits/outros).</summary>
    private const double CompleteThreshold = 0.92;

    public async Task<Dictionary<string, LessonProgress>> ForCourseAsync(int userId, Course course)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Progress.AsNoTracking().Where(p => p.UserId == userId && p.CoursePath == course.FolderName).ToListAsync();
        return rows.ToDictionary(r => r.LessonPath);
    }

    public async Task<Dictionary<string, CourseStats>> StatsAsync(int userId, IReadOnlyList<Course> courses)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Progress.AsNoTracking().Where(p => p.UserId == userId).ToListAsync();
        var byCourse = rows.GroupBy(r => r.CoursePath).ToDictionary(g => g.Key, g => g.ToDictionary(r => r.LessonPath));

        var result = new Dictionary<string, CourseStats>();
        foreach (var c in courses)
        {
            byCourse.TryGetValue(c.FolderName, out var progress);
            result[c.Id] = Stats(c, progress ?? []);
        }
        return result;
    }

    public static CourseStats Stats(Course course, IReadOnlyDictionary<string, LessonProgress> progress)
    {
        var lessons = course.AllLessons.ToList();
        var completed = lessons.Count(l => progress.TryGetValue(l.RelativePath, out var p) && p.Completed);
        var last = lessons
            .Select(l => (lesson: l, p: progress.GetValueOrDefault(l.RelativePath)))
            .Where(x => x.p is not null)
            .OrderByDescending(x => x.p!.UpdatedAt)
            .FirstOrDefault();

        Lesson? resume = null;
        if (last.lesson is not null)
        {
            // Resume the last touched lesson, or the next one if it was finished.
            var idx = lessons.IndexOf(last.lesson);
            resume = last.p!.Completed
                ? lessons.Skip(idx + 1).FirstOrDefault(l => !(progress.GetValueOrDefault(l.RelativePath)?.Completed ?? false)) ?? last.lesson
                : last.lesson;
        }
        return new CourseStats(completed, lessons.Count, resume, last.p?.UpdatedAt);
    }

    /// <summary>Saves the playback position; marks complete automatically near the end.</summary>
    public async Task<LessonProgress> SavePositionAsync(int userId, Course course, Lesson lesson, double position, double duration, double watchedSeconds = 0)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await GetOrCreate(db, userId, course, lesson);
        if (watchedSeconds > 0)
        {
            // Wall-clock time spent watching, for the stats page. Capped so one bad report can't skew it.
            var day = DateTime.Now.ToString("yyyy-MM-dd");
            var w = await db.WatchDays.FindAsync(userId, day, course.FolderName);
            if (w is null) db.WatchDays.Add(w = new WatchDay { UserId = userId, Day = day, CoursePath = course.FolderName });
            w.Seconds += Math.Min(watchedSeconds, 120);
        }
        row.PositionSeconds = position;
        if (duration > 0) row.DurationSeconds = duration;
        if (duration > 0 && position / duration >= CompleteThreshold) row.Completed = true;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return row;
    }

    public async Task<LessonProgress> SetCompletedAsync(int userId, Course course, Lesson lesson, bool completed)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await GetOrCreate(db, userId, course, lesson);
        row.Completed = completed;
        if (!completed) row.PositionSeconds = 0;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return row;
    }

    public async Task ResetCourseAsync(int userId, Course course)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Progress.Where(p => p.UserId == userId && p.CoursePath == course.FolderName).ExecuteDeleteAsync();
    }

    private static async Task<LessonProgress> GetOrCreate(ProgressDb db, int userId, Course course, Lesson lesson)
    {
        var row = await db.Progress.FindAsync(userId, lesson.RelativePath);
        if (row is null)
        {
            row = new LessonProgress { UserId = userId, LessonPath = lesson.RelativePath, CoursePath = course.FolderName };
            db.Progress.Add(row);
        }
        return row;
    }
}
