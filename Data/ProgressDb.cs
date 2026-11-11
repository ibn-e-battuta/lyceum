using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CourseHub.Data;

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
    /// <summary>Changes whenever the password or role changes, which signs the user out everywhere.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>Watch progress for one lesson, keyed by user and the lesson's path relative to the courses folder.</summary>
public class LessonProgress
{
    public int UserId { get; set; }
    public string LessonPath { get; set; } = "";
    public string CoursePath { get; set; } = "";
    public double PositionSeconds { get; set; }
    public double DurationSeconds { get; set; }
    public bool Completed { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public enum NoteKind { Note = 0, Bookmark = 1 }

/// <summary>A timestamped note or bookmark on a lesson.</summary>
public class Note
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public NoteKind Kind { get; set; }
    public string CoursePath { get; set; } = "";
    public string LessonPath { get; set; } = "";
    public double Seconds { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Seconds actually spent watching, per user, day and course.</summary>
public class WatchDay
{
    public int UserId { get; set; }
    public string Day { get; set; } = ""; // yyyy-MM-dd, server local time
    public string CoursePath { get; set; } = "";
    public double Seconds { get; set; }
}

/// <summary>Per-user course preferences: favourite, archived, skip-intro.</summary>
public class CourseMeta
{
    public int UserId { get; set; }
    public string CoursePath { get; set; } = "";
    public bool Favorite { get; set; }
    public bool Archived { get; set; }
    public double SkipIntroSeconds { get; set; }
}

public class LearningPath
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<LearningPathItem> Items { get; set; } = [];
}

public class LearningPathItem
{
    public int Id { get; set; }
    public int LearningPathId { get; set; }
    public string CoursePath { get; set; } = "";
    public int Order { get; set; }
}

/// <summary>One subtitle cue, indexed for transcript search (shared by all users).</summary>
public class TranscriptCue
{
    public int Id { get; set; }
    public string LessonPath { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>Which subtitle file was indexed for a lesson, so we only re-index when it changes.</summary>
public class TranscriptSource
{
    public string LessonPath { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long FileTicks { get; set; }
}

public class ProgressDb(DbContextOptions<ProgressDb> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<LessonProgress> Progress => Set<LessonProgress>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<WatchDay> WatchDays => Set<WatchDay>();
    public DbSet<CourseMeta> CourseMeta => Set<CourseMeta>();
    public DbSet<LearningPath> Paths => Set<LearningPath>();
    public DbSet<LearningPathItem> PathItems => Set<LearningPathItem>();
    public DbSet<TranscriptCue> Cues => Set<TranscriptCue>();
    public DbSet<TranscriptSource> TranscriptSources => Set<TranscriptSource>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.Property(u => u.Username).UseCollation("NOCASE");
            e.HasIndex(u => u.Username).IsUnique();
        });
        b.Entity<LessonProgress>(e =>
        {
            e.HasKey(p => new { p.UserId, p.LessonPath });
            e.HasIndex(p => new { p.UserId, p.CoursePath });
            e.HasIndex(p => new { p.UserId, p.UpdatedAt });
        });
        b.Entity<Note>(e =>
        {
            e.HasIndex(n => new { n.UserId, n.CoursePath });
            e.HasIndex(n => new { n.UserId, n.LessonPath });
        });
        b.Entity<WatchDay>().HasKey(w => new { w.UserId, w.Day, w.CoursePath });
        b.Entity<CourseMeta>().HasKey(m => new { m.UserId, m.CoursePath });
        b.Entity<LearningPath>(e =>
        {
            e.HasIndex(p => p.UserId);
            e.HasMany(p => p.Items).WithOne().HasForeignKey(i => i.LearningPathId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<TranscriptCue>().HasIndex(c => c.LessonPath);
        b.Entity<TranscriptSource>().HasKey(s => s.LessonPath);
    }

    /// <summary>
    /// Applies migrations. A database from the early single-user builds (no migration history) is
    /// backed up and its progress, notes, stats, preferences and paths are copied into the new schema
    /// as user #1 — the first account created on the setup page.
    /// </summary>
    public async Task MigrateWithLegacyImportAsync(ILogger logger)
    {
        var conn = (SqliteConnection)Database.GetDbConnection();
        var file = new SqliteConnectionStringBuilder(conn.ConnectionString).DataSource;
        string? legacyCopy = null;

        if (File.Exists(file))
        {
            await conn.OpenAsync();
            bool hasHistory, hasProgress;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('__EFMigrationsHistory','Progress')";
                var names = new List<string>();
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) names.Add(r.GetString(0));
                hasHistory = names.Contains("__EFMigrationsHistory");
                hasProgress = names.Contains("Progress");
            }
            await conn.CloseAsync();

            if (!hasHistory && hasProgress)
            {
                SqliteConnection.ClearAllPools();
                legacyCopy = file + $".pre-accounts-{DateTime.Now:yyyyMMddHHmmss}.bak";
                File.Move(file, legacyCopy);
                foreach (var ext in new[] { "-wal", "-shm" })
                    if (File.Exists(file + ext)) File.Move(file + ext, legacyCopy + ext);
                logger.LogWarning("Upgrading a database from an older CourseHub. The original is kept at {Backup}", legacyCopy);
            }
        }

        await Database.MigrateAsync();

        if (legacyCopy is null) return;
        await conn.OpenAsync();
        try
        {
            await Exec(conn, $"ATTACH DATABASE '{legacyCopy.Replace("'", "''")}' AS legacy");
            var legacyTables = new List<string>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM legacy.sqlite_master WHERE type='table'";
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) legacyTables.Add(r.GetString(0));
            }
            if (legacyTables.Contains("Progress"))
                await Exec(conn, """INSERT INTO Progress (UserId, LessonPath, CoursePath, PositionSeconds, DurationSeconds, Completed, UpdatedAt) SELECT 1, LessonPath, CoursePath, PositionSeconds, DurationSeconds, Completed, UpdatedAt FROM legacy.Progress""");
            if (legacyTables.Contains("Notes"))
                await Exec(conn, """INSERT INTO Notes (UserId, Kind, CoursePath, LessonPath, Seconds, Text, CreatedAt, UpdatedAt) SELECT 1, Kind, CoursePath, LessonPath, Seconds, Text, CreatedAt, UpdatedAt FROM legacy.Notes""");
            if (legacyTables.Contains("WatchDays"))
                await Exec(conn, """INSERT INTO WatchDays (UserId, Day, CoursePath, Seconds) SELECT 1, Day, CoursePath, Seconds FROM legacy.WatchDays""");
            if (legacyTables.Contains("CourseMeta"))
                await Exec(conn, """INSERT INTO CourseMeta (UserId, CoursePath, Favorite, Archived, SkipIntroSeconds) SELECT 1, CoursePath, Favorite, Archived, SkipIntroSeconds FROM legacy.CourseMeta""");
            if (legacyTables.Contains("Paths") && legacyTables.Contains("PathItems"))
            {
                await Exec(conn, """INSERT INTO Paths (Id, UserId, Name, CreatedAt) SELECT Id, 1, Name, CreatedAt FROM legacy.Paths""");
                await Exec(conn, """INSERT INTO PathItems (Id, LearningPathId, CoursePath, "Order") SELECT Id, LearningPathId, CoursePath, "Order" FROM legacy.PathItems""");
            }
            await Exec(conn, "DETACH DATABASE legacy");
            logger.LogInformation("Imported data from the old database; it will belong to the first account you create");
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private static async Task Exec(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>Used by `dotnet ef` to create migrations.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<ProgressDb>
{
    public ProgressDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ProgressDb>().UseSqlite("Data Source=design.db").Options);
}
