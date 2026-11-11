using System.Net.Http.Headers;
using System.Text;
using CourseHub.Data;

namespace CourseHub.Services;

/// <summary>Sends each person their daily nudge (via their own ntfy topic) if they haven't met their goal yet.</summary>
public sealed class ReminderService(
    UserService users,
    WatchTimeService watchTime,
    ReviewService reviews,
    ProgressService progress,
    CourseLibrary library,
    AppSettingsService settings,
    GuestService guests,
    IHttpClientFactory httpFactory,
    ILogger<ReminderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var now = DateTime.Now;
                var today = now.ToString("yyyy-MM-dd");
                if (now.Minute == 0 && now.Second < 30) await guests.CleanupAsync(); // hourly: drop long-expired guest accounts
                foreach (var u in await users.DueForReminderAsync(now.ToString("HH:mm"), today))
                {
                    await users.MarkRemindedAsync(u.Id, today); // once per day, even if sending fails
                    var info = UserInfo.From(u);
                    var (title, message, link) = await ComposeAsync(info);
                    if (title is null) continue; // goal already met and nothing due
                    var error = await SendAsync(u.ReminderUrl!, u.ReminderToken, title, message!, link);
                    if (error is not null) logger.LogWarning("Reminder for {User} failed: {Error}", u.Username, error);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reminder check failed");
            }
        }
    }

    /// <summary>Builds today's reminder; title is null when there's nothing to remind about.</summary>
    public async Task<(string? Title, string? Message, string? Link)> ComposeAsync(UserInfo user, bool force = false)
    {
        var watchedMin = (int)Math.Round(await watchTime.TodaySecondsAsync(user.Id) / 60);
        var goal = user.DailyGoalMinutes;
        var due = (await reviews.CountsAsync(user.Id, user.Scope)).Due;
        var goalMet = goal is null || watchedMin >= goal;
        if (goalMet && due == 0 && !force) return (null, null, null);

        var parts = new List<string>();
        if (goal is not null) parts.Add(goalMet ? $"Goal met: {watchedMin} of {goal} minutes today. 🎉" : $"You've watched {watchedMin} of {goal} minutes today.");
        if (due > 0) parts.Add($"{due} flashcard{(due == 1 ? "" : "s")} to review.");

        // Link to the course you were last watching.
        var courses = library.CoursesFor(user.Scope).ToList();
        var stats = await progress.StatsAsync(user.Id, courses);
        var resume = courses.Where(c => stats[c.Id].Started && stats[c.Id].Completed < stats[c.Id].Total).MaxBy(c => stats[c.Id].LastWatched);
        if (resume is not null) parts.Add($"Continue: {resume.Title}");
        var publicUrl = (await settings.GetAsync<ServerSettings>()).PublicUrl?.TrimEnd('/');
        var link = publicUrl is null ? null : resume is null ? publicUrl + "/" : $"{publicUrl}/course/{resume.Id}";
        return (goalMet ? "Keep it up" : "Time to learn", string.Join(" ", parts), link);
    }

    public async Task<string?> SendAsync(string url, string? token, string title, string message, string? link)
    {
        try
        {
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(message, Encoding.UTF8, "text/plain") };
            if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("Title", "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(title)) + "?=");
            req.Headers.TryAddWithoutValidation("Tags", "books");
            if (link is not null) req.Headers.TryAddWithoutValidation("Click", link);
            using var resp = await http.SendAsync(req);
            return resp.IsSuccessStatusCode ? null : $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
