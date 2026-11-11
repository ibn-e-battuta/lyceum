using System.Net.Http.Headers;
using System.Text;
using CourseHub.Data;

namespace CourseHub.Services;

public sealed record Notification(string Event, string Title, string Message, string? Path, object? Data);

/// <summary>Sends events to webhooks and ntfy topics configured in Settings.</summary>
public sealed class NotificationService(
    AppSettingsService settings,
    IHttpClientFactory httpFactory,
    UserService users,
    ILogger<NotificationService> logger) : IHostedService
{
    private readonly List<Action> _unsubscribe = [];

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) { foreach (var u in _unsubscribe) u(); return Task.CompletedTask; }

    /// <summary>Wired up in Program.cs once all services exist.</summary>
    public void Subscribe(CourseLibrary library, ActivityService activity, TranscriptService transcripts)
    {
        Action<LibraryDiff> onDiff = d => _ = OnDiffAsync(d);
        library.Diff += onDiff;
        _unsubscribe.Add(() => library.Diff -= onDiff);

        Action<ActivityEvent, Course> onActivity = (e, c) => _ = OnActivityAsync(e, c);
        activity.Recorded += onActivity;
        _unsubscribe.Add(() => activity.Recorded -= onActivity);

        Action<int> onDrained = n => _ = SendAsync(t => t.OnTranscriptsDone, new Notification("transcripts.completed",
            "Transcripts ready", $"Finished transcribing {n} lesson{(n == 1 ? "" : "s")}.", "/settings", new { lessons = n }));
        transcripts.QueueDrained += onDrained;
        _unsubscribe.Add(() => transcripts.QueueDrained -= onDrained);
    }

    private async Task OnDiffAsync(LibraryDiff diff)
    {
        foreach (var c in diff.AddedCourses)
            await SendAsync(t => t.OnCourseAdded, new Notification("course.added", "New course: " + c.Title,
                $"{c.LessonCount} lesson{(c.LessonCount == 1 ? "" : "s")}{(c.TotalSeconds > 0 ? $", {Components.Format.Long(c.TotalSeconds)}" : "")}", $"/course/{c.Id}", CourseData(c)));
        foreach (var c in diff.RemovedCourses)
            await SendAsync(t => t.OnCourseRemoved, new Notification("course.removed", "Course removed: " + c.Title,
                $"{c.Title} is no longer in the library.", null, CourseData(c)));
    }

    private async Task OnActivityAsync(ActivityEvent e, Course c)
    {
        if (e.Type != ActivityType.CourseCompleted) return;
        var user = await users.GetAsync(e.UserId);
        if (user is null || !user.ShareActivity) return;
        await SendAsync(t => t.OnCourseCompleted, new Notification("course.completed", $"🎓 {user.Name} finished {c.Title}",
            $"{user.Name} completed all {c.LessonCount} lessons of {c.Title}.", $"/course/{c.Id}", new { user = user.Name, course = CourseData(c) }));
    }

    private static object CourseData(Course c) => new { id = c.Id, title = c.Title, lessons = c.LessonCount, durationSeconds = (int)c.TotalSeconds, libraryId = c.LibraryId };

    public async Task SendAsync(Func<NotificationTarget, bool> wants, Notification n)
    {
        var all = await settings.GetAsync<NotificationSettings>();
        foreach (var t in all.Targets.Where(wants))
        {
            var error = await SendToAsync(t, n);
            if (error is not null) logger.LogWarning("Notification to {Url} failed: {Error}", t.Url, error);
        }
    }

    /// <summary>Returns an error message, or null when it was delivered.</summary>
    public async Task<string?> SendToAsync(NotificationTarget target, Notification n)
    {
        try
        {
            var server = await settings.GetAsync<ServerSettings>();
            var link = n.Path is not null && !string.IsNullOrWhiteSpace(server.PublicUrl) ? server.PublicUrl!.TrimEnd('/') + n.Path : null;
            using var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            using var req = new HttpRequestMessage(HttpMethod.Post, target.Url);
            if (!string.IsNullOrWhiteSpace(target.Token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);

            if (target.Type == "ntfy")
            {
                req.Content = new StringContent(n.Message, Encoding.UTF8, "text/plain");
                // Header values must be ASCII-safe; ntfy decodes RFC 2047 encoded titles.
                req.Headers.TryAddWithoutValidation("Title", "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(n.Title)) + "?=");
                req.Headers.TryAddWithoutValidation("Tags", n.Event switch
                {
                    "course.added" => "books",
                    "course.completed" => "mortar_board",
                    "transcripts.completed" => "memo",
                    _ => "bell",
                });
                if (link is not null) req.Headers.TryAddWithoutValidation("Click", link);
            }
            else
            {
                req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new
                {
                    @event = n.Event, title = n.Title, message = n.Message, url = link, data = n.Data, timestamp = DateTime.UtcNow,
                }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
            }
            using var resp = await http.SendAsync(req);
            return resp.IsSuccessStatusCode ? null : $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
