using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lyceum.Data;

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
    /// <summary>Shown on certificates and the activity feed; falls back to the username.</summary>
    public string? DisplayName { get; set; }
    /// <summary>Comma-separated library ids this user can see; null means every library. Admins always see all.</summary>
    public string? LibraryAccess { get; set; }
    /// <summary>Show this user's course starts/completions in the household activity feed.</summary>
    public bool ShareActivity { get; set; } = true;

    // ---- two-factor sign-in (TOTP) ----
    /// <summary>Base32 secret, encrypted with ASP.NET Data Protection.</summary>
    public string? TotpSecret { get; set; }
    public bool TotpEnabled { get; set; }
    /// <summary>Last accepted 30-second time step, so a code can't be replayed.</summary>
    public long TotpLastStep { get; set; }
    /// <summary>SHA-256 hashes of unused recovery codes, separated by ';'.</summary>
    public string? RecoveryCodes { get; set; }

    // ---- kids' profiles ----
    public bool IsKid { get; set; }
    /// <summary>Daily watch-time limit for kids; null means unlimited.</summary>
    public int? DailyLimitMinutes { get; set; }

    // ---- goals & reminders ----
    public int? DailyGoalMinutes { get; set; }
    /// <summary>Local time ("HH:mm") to send a reminder when the goal isn't met yet.</summary>
    public string? ReminderTime { get; set; }
    /// <summary>Personal ntfy topic URL for reminders.</summary>
    public string? ReminderUrl { get; set; }
    public string? ReminderToken { get; set; }
    public string? LastReminderDay { get; set; }

    // ---- guest links ----
    /// <summary>Hidden account behind a guest link: can open only <see cref="GuestCourse"/> until <see cref="GuestExpiresAt"/>.</summary>
    public bool IsGuest { get; set; }
    public string? GuestCourse { get; set; }
    public DateTime? GuestExpiresAt { get; set; }
}

/// <summary>A folder of courses. Library #1 is the one from configuration (Lyceum:CoursesRoot).</summary>
public class LibraryEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class ApiKey
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>First characters of the key, shown so people can tell keys apart.</summary>
    public string Prefix { get; set; } = "";
    /// <summary>SHA-256 of the full key. The key itself is only shown once.</summary>
    public string KeyHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

/// <summary>Links an account at an OpenID Connect provider (Authentik, Authelia, Keycloak…) to a local user.</summary>
public class ExternalLogin
{
    public string Provider { get; set; } = "";
    public string Subject { get; set; } = "";
    public int UserId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Runtime settings that admins change in the UI (AI provider, notifications, transcoding…).</summary>
public class AppSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public enum ActivityType { CourseStarted = 0, LessonCompleted = 1, CourseCompleted = 2 }

public class ActivityEvent
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public ActivityType Type { get; set; }
    public string CoursePath { get; set; } = "";
    public string? LessonPath { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Cached AI output for a lesson (summary, quiz, flashcards), shared by everyone.</summary>
public class AiContent
{
    public int Id { get; set; }
    public string LessonPath { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Content { get; set; } = "";
    public string Model { get; set; } = "";
    public DateTime CreatedAt { get; set; }
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
    /// <summary>Visible to everyone in the household who can open the course.</summary>
    public bool Shared { get; set; }
}

/// <summary>A household comment on a lesson, optionally at a moment in the video.</summary>
public class LessonComment
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string CoursePath { get; set; } = "";
    public string LessonPath { get; set; } = "";
    public double? Seconds { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>Time-limited link that lets someone without an account watch one course.</summary>
public class GuestLink
{
    public int Id { get; set; }
    public string TokenHash { get; set; } = "";
    public string CoursePath { get; set; } = "";
    public string Label { get; set; } = "";
    public int CreatedByUserId { get; set; }
    /// <summary>The hidden guest account that the link signs into.</summary>
    public int? GuestUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public int Visits { get; set; }
}

/// <summary>A flashcard in someone's spaced-repetition deck (SM-2 scheduling).</summary>
public class ReviewCard
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string CoursePath { get; set; } = "";
    public string? LessonPath { get; set; }
    public string Front { get; set; } = "";
    public string Back { get; set; } = "";
    public DateTime Due { get; set; }
    public double IntervalDays { get; set; }
    public double Ease { get; set; } = 2.5;
    public int Repetitions { get; set; }
    public int Lapses { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastReviewedAt { get; set; }
}

/// <summary>Embedding of ~45 seconds of transcript, for searching a course by meaning.</summary>
public class PassageEmbedding
{
    public int Id { get; set; }
    public string LessonPath { get; set; } = "";
    public double Start { get; set; }
    public string Text { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>float32 vector, little-endian.</summary>
    public byte[] Vector { get; set; } = [];
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
    public DbSet<LibraryEntity> Libraries => Set<LibraryEntity>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<ExternalLogin> ExternalLogins => Set<ExternalLogin>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<ActivityEvent> Activity => Set<ActivityEvent>();
    public DbSet<AiContent> AiContent => Set<AiContent>();
    public DbSet<LessonComment> Comments => Set<LessonComment>();
    public DbSet<GuestLink> GuestLinks => Set<GuestLink>();
    public DbSet<ReviewCard> ReviewCards => Set<ReviewCard>();
    public DbSet<PassageEmbedding> Embeddings => Set<PassageEmbedding>();

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
        b.Entity<LibraryEntity>().ToTable("Libraries");
        b.Entity<ApiKey>(e =>
        {
            e.HasIndex(k => k.KeyHash).IsUnique();
            e.HasIndex(k => k.UserId);
        });
        b.Entity<ExternalLogin>(e =>
        {
            e.HasKey(x => new { x.Provider, x.Subject });
            e.HasIndex(x => x.UserId);
        });
        b.Entity<AppSetting>().HasKey(x => x.Key);
        b.Entity<ActivityEvent>(e =>
        {
            e.HasIndex(a => a.CreatedAt);
            e.HasIndex(a => new { a.UserId, a.CoursePath, a.Type });
        });
        b.Entity<AiContent>().HasIndex(a => new { a.LessonPath, a.Kind }).IsUnique();
        b.Entity<LessonComment>().HasIndex(c => c.LessonPath);
        b.Entity<GuestLink>().HasIndex(g => g.TokenHash).IsUnique();
        b.Entity<ReviewCard>(e =>
        {
            e.HasIndex(c => new { c.UserId, c.Due });
            e.HasIndex(c => new { c.UserId, c.Front });
        });
        b.Entity<PassageEmbedding>().HasIndex(p => new { p.LessonPath, p.Model });
    }
}

/// <summary>Used by `dotnet ef` to create migrations.</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<ProgressDb>
{
    public ProgressDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ProgressDb>().UseSqlite("Data Source=design.db").Options);
}
