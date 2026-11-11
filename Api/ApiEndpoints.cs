using System.Security.Claims;
using CourseHub.Data;
using CourseHub.Services;

namespace CourseHub.Api;

// ---------- response shapes ----------

public sealed record MeDto(int Id, string Username, string DisplayName, bool IsAdmin);
public sealed record LibraryDto(int Id, string Name, int Courses);
public sealed record CourseSummaryDto(string Id, string Title, string? Author, int LibraryId, int Lessons, int DurationSeconds,
    int Completed, double Percent, DateTime? LastWatched, string? ResumeLessonId, string CoverUrl);
public sealed record LessonDto(string Id, string Title, string Kind, int? DurationSeconds, string MediaUrl, string? ThumbUrl,
    double Position, bool Completed);
public sealed record SectionDto(string Title, string? Group, IReadOnlyList<LessonDto> Lessons);
public sealed record CourseDto(string Id, string Title, string? Author, string? Description, int LibraryId, string CoverUrl,
    IReadOnlyList<SectionDto> Sections, double Percent);
public sealed record TrackDto(string Language, string Label, string Url);
public sealed record AttachmentDto(string Name, string Url, long Size);
public sealed record LessonDetailDto(LessonDto Lesson, string CourseId, IReadOnlyList<TrackDto> Subtitles,
    IReadOnlyList<AttachmentDto> Attachments, IReadOnlyList<Chapter> Chapters, string? PreviousLessonId, string? NextLessonId);
public sealed record ProgressUpdate(double Position, double Duration, bool? Completed, double? WatchedSeconds);
public sealed record NoteDto(int Id, string LessonId, string Kind, double Seconds, string Text, DateTime UpdatedAt);
public sealed record NoteCreate(double Seconds, string? Text, string? Kind);
public sealed record SearchHitDto(string Type, string CourseId, string? LessonId, string Title, string? Snippet, double? At);
public sealed record StatsDto(int CurrentStreak, int LongestStreak, double TotalSeconds, double Last7DaysSeconds, double DailyAverageSeconds);
public sealed record ActivityDto(string User, string Type, string CourseId, string CourseTitle, string? LessonTitle, int Count, DateTime At);

/// <summary>REST API under /api/v1. Authenticate with the browser session or an API key (Settings → API keys).</summary>
public static class ApiEndpoints
{
    public static void MapCourseHubApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").WithTags("CourseHub").DisableAntiforgery();

        api.MapGet("/me", async (ClaimsPrincipal p, UserService users) =>
        {
            var u = await users.GetAsync(p.GetUserId()!.Value);
            return u is null ? Results.Unauthorized() : Results.Ok(new MeDto(u.Id, u.Username, u.Name, u.IsAdmin));
        });

        api.MapGet("/libraries", async (ClaimsPrincipal p, UserService users, CourseLibrary lib) =>
        {
            var access = await users.AllowedLibrariesAsync(p.GetUserId()!.Value);
            return lib.Libraries.Where(l => access is null || access.Contains(l.Id))
                .Select(l => new LibraryDto(l.Id, l.Name, lib.Courses.Count(c => c.LibraryId == l.Id)));
        });

        api.MapGet("/courses", async (int? library, ClaimsPrincipal p, UserService users, CourseLibrary lib, ProgressService progress) =>
        {
            var userId = p.GetUserId()!.Value;
            var courses = lib.CoursesFor(await users.AllowedLibrariesAsync(userId)).Where(c => library is null || c.LibraryId == library).ToList();
            var stats = await progress.StatsAsync(userId, courses);
            return courses.Select(c => Summary(c, stats[c.Id]));
        });

        api.MapGet("/continue", async (ClaimsPrincipal p, UserService users, CourseLibrary lib, ProgressService progress) =>
        {
            var userId = p.GetUserId()!.Value;
            var courses = lib.CoursesFor(await users.AllowedLibrariesAsync(userId)).ToList();
            var stats = await progress.StatsAsync(userId, courses);
            return courses.Where(c => stats[c.Id].Started && stats[c.Id].Completed < stats[c.Id].Total)
                .OrderByDescending(c => stats[c.Id].LastWatched).Take(20).Select(c => Summary(c, stats[c.Id]));
        });

        api.MapGet("/courses/{id}", async (string id, ClaimsPrincipal p, UserService users, CourseLibrary lib, ProgressService progress) =>
        {
            var userId = p.GetUserId()!.Value;
            var course = lib.GetCourse(id);
            if (course is null || !CourseLibrary.CanAccess(course, await users.AllowedLibrariesAsync(userId))) return Results.NotFound();
            var prog = await progress.ForCourseAsync(userId, course);
            var stats = ProgressService.Stats(course, prog);
            return Results.Ok(new CourseDto(course.Id, course.Title, course.Author, course.Description, course.LibraryId, course.CoverUrl,
                course.Sections.Select(s => new SectionDto(s.Title, s.Group, s.Lessons.Select(l => Lesson(l, prog)).ToList())).ToList(), stats.Percent));
        });

        api.MapGet("/lessons/{id}", async (string id, ClaimsPrincipal p, UserService users, CourseLibrary lib, ProgressService progress,
            TranscriptService transcripts, MediaProbe probe) =>
        {
            var userId = p.GetUserId()!.Value;
            if (await Find(id, userId, users, lib) is not { } x) return Results.NotFound();
            var prog = await progress.ForCourseAsync(userId, x.Course);
            var all = x.Course.AllLessons.ToList();
            var i = all.IndexOf(x.Lesson);
            var chapters = x.Lesson.Kind == LessonKind.Video ? (await probe.ProbeAsync(x.Lesson.FullPath))?.Chapters ?? [] : [];
            return Results.Ok(new LessonDetailDto(
                Lesson(x.Lesson, prog), x.Course.Id,
                transcripts.Tracks(x.Lesson).Select(t => new TrackDto(t.Language, t.Label, $"/subs/{x.Lesson.Id}.vtt{(t.IsOriginal ? "" : "?lang=" + t.Language)}")).ToList(),
                x.Lesson.Attachments.Select(a => new AttachmentDto(a.Name, a.Url, a.SizeBytes)).ToList(),
                chapters,
                i > 0 ? all[i - 1].Id : null,
                i < all.Count - 1 ? all[i + 1].Id : null));
        });

        api.MapPut("/lessons/{id}/progress", async (string id, ProgressUpdate body, ClaimsPrincipal p, UserService users, CourseLibrary lib, ProgressService progress) =>
        {
            var userId = p.GetUserId()!.Value;
            if (await Find(id, userId, users, lib) is not { } x) return Results.NotFound();
            var row = await progress.SavePositionAsync(userId, x.Course, x.Lesson, Math.Max(0, body.Position), Math.Max(0, body.Duration), Math.Clamp(body.WatchedSeconds ?? 0, 0, 120));
            if (body.Completed is { } done && done != row.Completed) row = await progress.SetCompletedAsync(userId, x.Course, x.Lesson, done);
            return Results.Ok(new { position = row.PositionSeconds, completed = row.Completed });
        });

        api.MapGet("/courses/{id}/notes", async (string id, ClaimsPrincipal p, UserService users, CourseLibrary lib, UserDataService data) =>
        {
            var userId = p.GetUserId()!.Value;
            var course = lib.GetCourse(id);
            if (course is null || !CourseLibrary.CanAccess(course, await users.AllowedLibrariesAsync(userId))) return Results.NotFound();
            var notes = await data.NotesForCourseAsync(userId, course);
            return Results.Ok(notes.Select(n => Note(n, lib)));
        });

        api.MapPost("/lessons/{id}/notes", async (string id, NoteCreate body, ClaimsPrincipal p, UserService users, CourseLibrary lib, UserDataService data) =>
        {
            var userId = p.GetUserId()!.Value;
            if (await Find(id, userId, users, lib) is not { } x) return Results.NotFound();
            var kind = body.Kind?.Equals("bookmark", StringComparison.OrdinalIgnoreCase) == true ? NoteKind.Bookmark : NoteKind.Note;
            if (kind == NoteKind.Note && string.IsNullOrWhiteSpace(body.Text)) return Results.BadRequest(new { error = "Notes need text." });
            var note = await data.AddNoteAsync(userId, x.Course, x.Lesson, kind, body.Seconds, body.Text ?? "");
            return Results.Created($"/api/v1/notes/{note.Id}", Note(note, lib));
        });

        api.MapDelete("/notes/{id:int}", async (int id, ClaimsPrincipal p, UserDataService data) =>
        {
            await data.DeleteNoteAsync(p.GetUserId()!.Value, id);
            return Results.NoContent();
        });

        api.MapGet("/search", async (string q, ClaimsPrincipal p, UserService users, CourseLibrary lib, TranscriptService transcripts) =>
        {
            var access = await users.AllowedLibrariesAsync(p.GetUserId()!.Value);
            q = q.Trim();
            var results = new List<SearchHitDto>();
            var courses = lib.CoursesFor(access).ToList();
            results.AddRange(courses.Where(c => c.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(20)
                .Select(c => new SearchHitDto("course", c.Id, null, c.Title, c.Author, null)));
            results.AddRange(courses.SelectMany(c => c.AllLessons.Select(l => (c, l))).Where(x => x.l.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(40)
                .Select(x => new SearchHitDto("lesson", x.c.Id, x.l.Id, x.l.Title, x.c.Title, null)));
            results.AddRange((await transcripts.SearchAsync(q, access)).Select(h => new SearchHitDto("transcript", h.Course.Id, h.Lesson.Id, h.Lesson.Title, h.Text, h.Start)));
            return results;
        });

        api.MapGet("/stats", async (ClaimsPrincipal p, UserService users, StatsService stats) =>
        {
            var userId = p.GetUserId()!.Value;
            var s = await stats.GetAsync(userId, await users.AllowedLibrariesAsync(userId));
            return new StatsDto(s.CurrentStreak, s.LongestStreak, s.TotalSeconds, s.Last7DaysSeconds, s.DailyAverageSeconds);
        });

        api.MapGet("/activity", async (ClaimsPrincipal p, UserService users, ActivityService activity) =>
        {
            var userId = p.GetUserId()!.Value;
            var feed = await activity.FeedAsync(userId, await users.AllowedLibrariesAsync(userId));
            return feed.Select(f => new ActivityDto(f.UserName, f.Type.ToString(), f.Course.Id, f.Course.Title, f.Lesson?.Title, f.Count, f.At));
        });

        api.MapPost("/libraries/scan", async (CourseLibrary lib) =>
        {
            await lib.RescanAsync();
            return Results.Ok(new { courses = lib.Courses.Count, lessons = lib.Courses.Sum(c => c.LessonCount) });
        }).RequireAuthorization("Admin");
    }

    private static async Task<(Course Course, Lesson Lesson)?> Find(string lessonId, int userId, UserService users, CourseLibrary lib)
    {
        var lesson = lib.GetLesson(lessonId);
        if (lesson is null || lib.CourseOf(lesson) is not { } course) return null;
        return CourseLibrary.CanAccess(course, await users.AllowedLibrariesAsync(userId)) ? (course, lesson) : null;
    }

    private static CourseSummaryDto Summary(Course c, CourseStats s) =>
        new(c.Id, c.Title, c.Author, c.LibraryId, c.LessonCount, (int)c.TotalSeconds, s.Completed, s.Percent, s.LastWatched, s.ResumeLesson?.Id, c.CoverUrl);

    private static LessonDto Lesson(Lesson l, IReadOnlyDictionary<string, LessonProgress> prog)
    {
        var p = prog.GetValueOrDefault(l.RelativePath);
        return new LessonDto(l.Id, l.Title, l.Kind == LessonKind.Video ? "video" : "article", l.DurationSeconds is { } d ? (int)d : null,
            l.MediaUrl, l.Kind == LessonKind.Video ? l.ThumbUrl : null, p?.PositionSeconds ?? 0, p?.Completed ?? false);
    }

    private static NoteDto Note(Note n, CourseLibrary lib) =>
        new(n.Id, lib.FindByPath(n.LessonPath)?.Lesson.Id ?? "", n.Kind == NoteKind.Bookmark ? "bookmark" : "note", n.Seconds, n.Text, n.UpdatedAt);
}
