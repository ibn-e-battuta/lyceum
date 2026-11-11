using CourseHub.Data;
using Microsoft.EntityFrameworkCore;

namespace CourseHub.Services;

public sealed record FeedItem(int UserId, string UserName, ActivityType Type, Course Course, Lesson? Lesson, int Count, DateTime At);

/// <summary>Course starts and completions, for the household feed, certificates and notifications.</summary>
public sealed class ActivityService(IDbContextFactory<ProgressDb> dbFactory, CourseLibrary library, UserService users)
{
    /// <summary>Raised for course starts/completions (not individual lessons).</summary>
    public event Action<ActivityEvent, Course>? Recorded;

    public async Task RecordAsync(int userId, ActivityType type, Course course, Lesson? lesson = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        // Starts and completions are one-off per course; don't log them twice (e.g. after un-ticking a lesson).
        if (type != ActivityType.LessonCompleted &&
            await db.Activity.AnyAsync(a => a.UserId == userId && a.CoursePath == course.FolderName && a.Type == type)) return;
        var e = new ActivityEvent
        {
            UserId = userId, Type = type, CoursePath = course.FolderName, LessonPath = lesson?.RelativePath, CreatedAt = DateTime.UtcNow,
        };
        db.Activity.Add(e);
        await db.SaveChangesAsync();
        if (type != ActivityType.LessonCompleted) Recorded?.Invoke(e, course);
    }

    public async Task<DateTime?> CompletedAtAsync(int userId, Course course)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Activity.Where(a => a.UserId == userId && a.CoursePath == course.FolderName && a.Type == ActivityType.CourseCompleted)
            .Select(a => (DateTime?)a.CreatedAt).FirstOrDefaultAsync();
    }

    /// <summary>
    /// Recent activity from everyone who shares it (plus the viewer), limited to courses the viewer can see.
    /// Lesson completions are grouped per person, course and day.
    /// </summary>
    public async Task<List<FeedItem>> FeedAsync(int viewerId, AccessScope? viewerAccess, int days = 14, int limit = 30)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var since = DateTime.UtcNow.AddDays(-days);
        var sharing = await db.Users.AsNoTracking().Where(u => u.ShareActivity || u.Id == viewerId)
            .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName!);
        var events = await db.Activity.AsNoTracking().Where(a => a.CreatedAt >= since).OrderByDescending(a => a.CreatedAt).Take(2000).ToListAsync();

        var items = new List<FeedItem>();
        foreach (var g in events.Where(e => sharing.ContainsKey(e.UserId))
                     .GroupBy(e => (e.UserId, e.CoursePath, e.Type, Day: e.Type == ActivityType.LessonCompleted ? e.CreatedAt.ToLocalTime().Date : e.CreatedAt)))
        {
            if (library.GetCourseByFolder(g.Key.CoursePath) is not { } course || !CourseLibrary.CanAccess(course, viewerAccess)) continue;
            var latest = g.First();
            var lesson = latest.LessonPath is null ? null : library.FindByPath(latest.LessonPath)?.Lesson;
            items.Add(new FeedItem(g.Key.UserId, sharing[g.Key.UserId], g.Key.Type, course, lesson, g.Count(), latest.CreatedAt));
        }
        return items.OrderByDescending(i => i.At).Take(limit).ToList();
    }

    public async Task<int> ActiveUserCountAsync() => (await users.ListAsync()).Count;
}
