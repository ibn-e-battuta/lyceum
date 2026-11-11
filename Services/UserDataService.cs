using System.Text;
using System.Text.Json;
using CourseHub.Data;
using Microsoft.EntityFrameworkCore;

namespace CourseHub.Services;

/// <summary>Notes, bookmarks, per-course preferences, learning paths, and backup/restore.</summary>
public sealed class UserDataService(IDbContextFactory<ProgressDb> dbFactory, CourseLibrary library)
{
    // ---------- notes & bookmarks ----------

    public async Task<List<Note>> NotesForCourseAsync(int userId, Course course)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Notes.AsNoTracking().Where(n => n.UserId == userId && n.CoursePath == course.FolderName)
            .OrderBy(n => n.LessonPath).ThenBy(n => n.Seconds).ToListAsync();
    }

    public async Task<List<Note>> AllNotesAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Notes.AsNoTracking().Where(n => n.UserId == userId).OrderByDescending(n => n.UpdatedAt).ToListAsync();
    }

    public async Task<Note> AddNoteAsync(int userId, Course course, Lesson lesson, NoteKind kind, double seconds, string text)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var note = new Note
        {
            UserId = userId, Kind = kind, CoursePath = course.FolderName, LessonPath = lesson.RelativePath,
            Seconds = Math.Max(0, seconds), Text = text.Trim(), CreatedAt = now, UpdatedAt = now,
        };
        db.Notes.Add(note);
        await db.SaveChangesAsync();
        return note;
    }

    public async Task UpdateNoteAsync(int userId, int id, string text)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);
        if (note is null) return;
        note.Text = text.Trim();
        note.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task DeleteNoteAsync(int userId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Notes.Where(n => n.Id == id && n.UserId == userId).ExecuteDeleteAsync();
    }

    /// <summary>All notes and bookmarks as Markdown, grouped by course and lesson in course order.</summary>
    public async Task<string> NotesMarkdownAsync(int userId, Course? onlyCourse = null)
    {
        var notes = await AllNotesAsync(userId);
        var sb = new StringBuilder();
        foreach (var course in library.Courses.Where(c => onlyCourse is null || c.Id == onlyCourse.Id))
        {
            var courseNotes = notes.Where(n => n.CoursePath == course.FolderName).ToList();
            if (courseNotes.Count == 0) continue;
            sb.AppendLine($"# {course.Title}").AppendLine();
            foreach (var lesson in course.AllLessons)
            {
                var ln = courseNotes.Where(n => n.LessonPath == lesson.RelativePath).OrderBy(n => n.Seconds).ToList();
                if (ln.Count == 0) continue;
                sb.AppendLine($"## {lesson.Title}").AppendLine();
                foreach (var n in ln)
                {
                    var stamp = lesson.Kind == LessonKind.Video ? $"[{Components.Format.Clock(Math.Max(n.Seconds, 0.5))}] " : "";
                    var icon = n.Kind == NoteKind.Bookmark ? "🔖 " : "";
                    var text = string.IsNullOrWhiteSpace(n.Text) ? "(bookmark)" : n.Text.Replace("\n", "\n  ");
                    sb.AppendLine($"- {icon}{stamp}{text}");
                }
                sb.AppendLine();
            }
        }
        return sb.Length == 0 ? "No notes yet.\n" : sb.ToString();
    }

    // ---------- per-course preferences ----------

    public async Task<Dictionary<string, CourseMeta>> AllMetaAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CourseMeta.AsNoTracking().Where(m => m.UserId == userId).ToDictionaryAsync(m => m.CoursePath);
    }

    public async Task<CourseMeta> MetaAsync(int userId, Course course)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CourseMeta.AsNoTracking().FirstOrDefaultAsync(m => m.UserId == userId && m.CoursePath == course.FolderName)
               ?? new CourseMeta { UserId = userId, CoursePath = course.FolderName };
    }

    public async Task<CourseMeta> UpdateMetaAsync(int userId, Course course, Action<CourseMeta> change)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var meta = await db.CourseMeta.FindAsync(userId, course.FolderName);
        if (meta is null) db.CourseMeta.Add(meta = new CourseMeta { UserId = userId, CoursePath = course.FolderName });
        change(meta);
        await db.SaveChangesAsync();
        return meta;
    }

    // ---------- learning paths ----------

    public async Task<List<LearningPath>> PathsAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var paths = await db.Paths.AsNoTracking().Where(p => p.UserId == userId).Include(p => p.Items).OrderBy(p => p.CreatedAt).ToListAsync();
        foreach (var p in paths) p.Items = p.Items.OrderBy(i => i.Order).ToList();
        return paths;
    }

    public async Task<LearningPath> CreatePathAsync(int userId, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var path = new LearningPath { UserId = userId, Name = name.Trim(), CreatedAt = DateTime.UtcNow };
        db.Paths.Add(path);
        await db.SaveChangesAsync();
        return path;
    }

    public async Task RenamePathAsync(int userId, int id, string name)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Paths.Where(p => p.Id == id && p.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, name.Trim()));
    }

    public async Task DeletePathAsync(int userId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await OwnsPath(db, userId, id)) return;
        await db.PathItems.Where(i => i.LearningPathId == id).ExecuteDeleteAsync();
        await db.Paths.Where(p => p.Id == id).ExecuteDeleteAsync();
    }

    public async Task AddToPathAsync(int userId, int pathId, Course course)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await OwnsPath(db, userId, pathId)) return;
        var items = await db.PathItems.Where(i => i.LearningPathId == pathId).ToListAsync();
        if (items.Any(i => i.CoursePath == course.FolderName)) return;
        db.PathItems.Add(new LearningPathItem
        {
            LearningPathId = pathId, CoursePath = course.FolderName, Order = items.Count == 0 ? 0 : items.Max(i => i.Order) + 1,
        });
        await db.SaveChangesAsync();
    }

    public async Task RemoveFromPathAsync(int userId, int itemId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var owned = db.Paths.Where(p => p.UserId == userId).Select(p => p.Id);
        await db.PathItems.Where(i => i.Id == itemId && owned.Contains(i.LearningPathId)).ExecuteDeleteAsync();
    }

    /// <summary>Moves an item one step up (-1) or down (+1).</summary>
    public async Task MoveInPathAsync(int userId, int pathId, int itemId, int direction)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (!await OwnsPath(db, userId, pathId)) return;
        var items = await db.PathItems.Where(i => i.LearningPathId == pathId).OrderBy(i => i.Order).ToListAsync();
        var idx = items.FindIndex(i => i.Id == itemId);
        var target = idx + direction;
        if (idx < 0 || target < 0 || target >= items.Count) return;
        (items[idx], items[target]) = (items[target], items[idx]);
        for (var i = 0; i < items.Count; i++) items[i].Order = i;
        await db.SaveChangesAsync();
    }

    private static Task<bool> OwnsPath(ProgressDb db, int userId, int pathId) =>
        db.Paths.AnyAsync(p => p.Id == pathId && p.UserId == userId);

    // ---------- backup / restore ----------

    public sealed record Backup(
        int Version,
        DateTime ExportedAt,
        List<LessonProgress> Progress,
        List<Note> Notes,
        List<WatchDay> WatchDays,
        List<CourseMeta> CourseMeta,
        List<BackupPath> Paths);

    public sealed record BackupPath(string Name, DateTime CreatedAt, List<string> Courses);

    private static readonly JsonSerializerOptions BackupJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<byte[]> ExportAsync(int userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var paths = await PathsAsync(userId);
        var backup = new Backup(
            1, DateTime.UtcNow,
            await db.Progress.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(),
            await db.Notes.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(),
            await db.WatchDays.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(),
            await db.CourseMeta.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(),
            paths.Select(p => new BackupPath(p.Name, p.CreatedAt, p.Items.Select(i => i.CoursePath).ToList())).ToList());
        return JsonSerializer.SerializeToUtf8Bytes(backup, BackupJson);
    }

    /// <summary>Replaces this user's progress, notes, stats, preferences and paths with the backup's contents.</summary>
    public async Task<string> ImportAsync(int userId, Stream json)
    {
        var backup = await JsonSerializer.DeserializeAsync<Backup>(json, BackupJson)
                     ?? throw new InvalidDataException("The file is empty.");
        if (backup.Version != 1) throw new InvalidDataException($"Unsupported backup version {backup.Version}.");

        await using var db = await dbFactory.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Progress.Where(x => x.UserId == userId).ExecuteDeleteAsync();
        await db.Notes.Where(x => x.UserId == userId).ExecuteDeleteAsync();
        await db.WatchDays.Where(x => x.UserId == userId).ExecuteDeleteAsync();
        await db.CourseMeta.Where(x => x.UserId == userId).ExecuteDeleteAsync();
        var oldPaths = db.Paths.Where(p => p.UserId == userId).Select(p => p.Id);
        await db.PathItems.Where(i => oldPaths.Contains(i.LearningPathId)).ExecuteDeleteAsync();
        await db.Paths.Where(p => p.UserId == userId).ExecuteDeleteAsync();

        // Backups are portable between accounts: everything is re-owned by the importing user.
        foreach (var x in backup.Progress ?? []) x.UserId = userId;
        foreach (var x in backup.Notes ?? []) { x.Id = 0; x.UserId = userId; }
        foreach (var x in backup.WatchDays ?? []) x.UserId = userId;
        foreach (var x in backup.CourseMeta ?? []) x.UserId = userId;
        db.Progress.AddRange(backup.Progress ?? []);
        db.Notes.AddRange(backup.Notes ?? []);
        db.WatchDays.AddRange(backup.WatchDays ?? []);
        db.CourseMeta.AddRange(backup.CourseMeta ?? []);
        foreach (var p in backup.Paths ?? [])
        {
            db.Paths.Add(new LearningPath
            {
                UserId = userId, Name = p.Name, CreatedAt = p.CreatedAt,
                Items = p.Courses.Select((c, i) => new LearningPathItem { CoursePath = c, Order = i }).ToList(),
            });
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return $"Restored {backup.Progress?.Count ?? 0} lesson records, {backup.Notes?.Count ?? 0} notes and {backup.Paths?.Count ?? 0} paths from {backup.ExportedAt.ToLocalTime():g}.";
    }
}
