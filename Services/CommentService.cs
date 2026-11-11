using Lyceum.Data;
using Microsoft.EntityFrameworkCore;

namespace Lyceum.Services;

public sealed record CommentView(int Id, int UserId, string Author, string Text, double? Seconds, DateTime CreatedAt);
public sealed record SharedNoteView(int Id, int UserId, string Author, string LessonPath, NoteKind Kind, double Seconds, string Text, DateTime UpdatedAt);

/// <summary>Household discussion: comments on lessons, and notes people chose to share.</summary>
public sealed class CommentService(IDbContextFactory<ProgressDb> dbFactory)
{
    public const int MaxLength = 2000;

    private static async Task<Dictionary<int, string>> NamesAsync(ProgressDb db) =>
        await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName!);

    public async Task<List<CommentView>> ForLessonAsync(Lesson lesson)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var names = await NamesAsync(db);
        return (await db.Comments.AsNoTracking().Where(c => c.LessonPath == lesson.RelativePath).OrderBy(c => c.CreatedAt).ToListAsync())
            .Select(c => new CommentView(c.Id, c.UserId, names.GetValueOrDefault(c.UserId, "?"), c.Text, c.Seconds, c.CreatedAt)).ToList();
    }

    public async Task<Dictionary<string, int>> CountsForCourseAsync(Course course)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Comments.AsNoTracking().Where(c => c.CoursePath == course.FolderName)
            .GroupBy(c => c.LessonPath).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public async Task<CommentView?> AddAsync(int userId, Course course, Lesson lesson, string text, double? seconds)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (text.Length > MaxLength) text = text[..MaxLength];
        await using var db = await dbFactory.CreateDbContextAsync();
        var c = new LessonComment { UserId = userId, CoursePath = course.FolderName, LessonPath = lesson.RelativePath, Text = text, Seconds = seconds, CreatedAt = DateTime.UtcNow };
        db.Comments.Add(c);
        await db.SaveChangesAsync();
        var names = await NamesAsync(db);
        return new CommentView(c.Id, userId, names.GetValueOrDefault(userId, "?"), c.Text, c.Seconds, c.CreatedAt);
    }

    /// <summary>People delete their own comments; admins can delete any.</summary>
    public async Task DeleteAsync(int commentId, int userId, bool isAdmin)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Comments.Where(c => c.Id == commentId && (isAdmin || c.UserId == userId)).ExecuteDeleteAsync();
    }

    /// <summary>Notes and bookmarks other people shared on this course.</summary>
    public async Task<List<SharedNoteView>> SharedNotesAsync(Course course, int exceptUserId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var names = await NamesAsync(db);
        return (await db.Notes.AsNoTracking().Where(n => n.CoursePath == course.FolderName && n.Shared && n.UserId != exceptUserId)
                .OrderBy(n => n.LessonPath).ThenBy(n => n.Seconds).ToListAsync())
            .Select(n => new SharedNoteView(n.Id, n.UserId, names.GetValueOrDefault(n.UserId, "?"), n.LessonPath, n.Kind, n.Seconds, n.Text, n.UpdatedAt)).ToList();
    }

    public async Task SetNoteSharedAsync(int userId, int noteId, bool shared)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Notes.Where(n => n.Id == noteId && n.UserId == userId).ExecuteUpdateAsync(x => x.SetProperty(n => n.Shared, shared));
    }
}
