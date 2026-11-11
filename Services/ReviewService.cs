using CourseHub.Data;
using Microsoft.EntityFrameworkCore;

namespace CourseHub.Services;

public enum ReviewGrade { Again = 0, Hard = 1, Good = 2, Easy = 3 }

public sealed record ReviewCounts(int Due, int Total, int ReviewedToday);

/// <summary>
/// Spaced repetition for flashcards: each card comes back just before you'd forget it.
/// Scheduling follows SM-2 as used by Anki (ease factor, growing intervals, lapses reset the card).
/// </summary>
public sealed class ReviewService(IDbContextFactory<ProgressDb> dbFactory, CourseLibrary library)
{
    private static readonly TimeSpan AgainDelay = TimeSpan.FromMinutes(10);

    /// <summary>Adds a lesson's flashcards to the user's deck, skipping ones already there. Returns how many were added.</summary>
    public async Task<int> AddAsync(int userId, Course course, Lesson? lesson, IEnumerable<Flashcard> cards)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = (await db.ReviewCards.Where(c => c.UserId == userId).Select(c => c.Front).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var card in cards)
        {
            if (string.IsNullOrWhiteSpace(card.Front) || string.IsNullOrWhiteSpace(card.Back) || !existing.Add(card.Front.Trim())) continue;
            db.ReviewCards.Add(new ReviewCard
            {
                UserId = userId, CoursePath = course.FolderName, LessonPath = lesson?.RelativePath,
                Front = card.Front.Trim(), Back = card.Back.Trim(), Due = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
            });
            added++;
        }
        await db.SaveChangesAsync();
        return added;
    }

    public async Task<ReviewCounts> CountsAsync(int userId, AccessScope? scope)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var cards = await db.ReviewCards.AsNoTracking().Where(c => c.UserId == userId)
            .Select(c => new { c.CoursePath, c.Due, c.LastReviewedAt }).ToListAsync();
        var visible = cards.Where(c => Visible(c.CoursePath, scope)).ToList();
        var startOfDay = DateTime.Now.Date.ToUniversalTime();
        return new ReviewCounts(visible.Count(c => c.Due <= DateTime.UtcNow), visible.Count, visible.Count(c => c.LastReviewedAt >= startOfDay));
    }

    /// <summary>Cards due now, oldest first; optionally only one course.</summary>
    public async Task<List<ReviewCard>> DueAsync(int userId, AccessScope? scope, string? coursePath = null, int take = 50)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTime.UtcNow;
        var due = await db.ReviewCards.AsNoTracking()
            .Where(c => c.UserId == userId && c.Due <= now && (coursePath == null || c.CoursePath == coursePath))
            .OrderBy(c => c.Due).Take(take * 2).ToListAsync();
        return due.Where(c => Visible(c.CoursePath, scope)).Take(take).ToList();
    }

    public async Task<List<ReviewCard>> AllAsync(int userId, AccessScope? scope)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return (await db.ReviewCards.AsNoTracking().Where(c => c.UserId == userId).OrderBy(c => c.CoursePath).ThenBy(c => c.CreatedAt).ToListAsync())
            .Where(c => Visible(c.CoursePath, scope)).ToList();
    }

    private bool Visible(string coursePath, AccessScope? scope) =>
        library.GetCourseByFolder(coursePath) is not { } c || CourseLibrary.CanAccess(c, scope);

    /// <summary>The interval each grade would give, for showing on the buttons ("10 min", "4 d"…).</summary>
    public static IReadOnlyDictionary<ReviewGrade, TimeSpan> Preview(ReviewCard card) =>
        Enum.GetValues<ReviewGrade>().ToDictionary(g => g, g => Schedule(card, g).Next);

    public async Task GradeAsync(int userId, int cardId, ReviewGrade grade)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var card = await db.ReviewCards.FirstOrDefaultAsync(c => c.Id == cardId && c.UserId == userId);
        if (card is null) return;
        var (next, interval, ease, reps, lapses) = Schedule(card, grade);
        card.IntervalDays = interval;
        card.Ease = ease;
        card.Repetitions = reps;
        card.Lapses = lapses;
        card.Due = DateTime.UtcNow.Add(next);
        card.LastReviewedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    private static (TimeSpan Next, double Interval, double Ease, int Reps, int Lapses) Schedule(ReviewCard c, ReviewGrade grade)
    {
        var ease = c.Ease <= 0 ? 2.5 : c.Ease;
        switch (grade)
        {
            case ReviewGrade.Again:
                // Forgotten: see it again in a few minutes, then start over with a lower ease.
                return (AgainDelay, 0, Math.Max(1.3, ease - 0.2), 0, c.Repetitions > 0 ? c.Lapses + 1 : c.Lapses);
            case ReviewGrade.Hard:
            {
                var interval = c.Repetitions == 0 ? 1 : Math.Max(1, c.IntervalDays * 1.2);
                return (TimeSpan.FromDays(interval), interval, Math.Max(1.3, ease - 0.15), c.Repetitions + 1, c.Lapses);
            }
            case ReviewGrade.Easy:
            {
                var interval = c.Repetitions == 0 ? 4 : Math.Max(c.IntervalDays + 1, c.IntervalDays * ease * 1.3);
                return (TimeSpan.FromDays(interval), interval, ease + 0.15, c.Repetitions + 1, c.Lapses);
            }
            default:
            {
                var interval = c.Repetitions switch { 0 => 1, 1 => 3, _ => Math.Max(c.IntervalDays + 1, c.IntervalDays * ease) };
                return (TimeSpan.FromDays(interval), interval, ease, c.Repetitions + 1, c.Lapses);
            }
        }
    }

    public async Task<ReviewCard> CreateAsync(int userId, Course course, string front, string back)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var card = new ReviewCard { UserId = userId, CoursePath = course.FolderName, Front = front.Trim(), Back = back.Trim(), Due = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };
        db.ReviewCards.Add(card);
        await db.SaveChangesAsync();
        return card;
    }

    public async Task DeleteAsync(int userId, int cardId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.ReviewCards.Where(c => c.Id == cardId && c.UserId == userId).ExecuteDeleteAsync();
    }

    /// <summary>"10 min", "3 d", "2 mo".</summary>
    public static string Human(TimeSpan t) =>
        t.TotalHours < 1 ? $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min"
        : t.TotalDays < 1 ? $"{(int)Math.Round(t.TotalHours)} h"
        : t.TotalDays < 30 ? $"{(int)Math.Round(t.TotalDays)} d"
        : t.TotalDays < 365 ? $"{t.TotalDays / 30:0.#} mo"
        : $"{t.TotalDays / 365:0.#} y";
}
