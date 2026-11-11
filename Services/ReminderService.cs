using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Lyceum.Data;

namespace Lyceum.Services;

/// <summary>Sends each person their daily nudge (via their own ntfy topic) if they haven't met their goal yet.</summary>
public sealed class ReminderService(
    UserService users,
    WatchTimeService watchTime,
    ReviewService reviews,
    ProgressService progress,
    CourseLibrary library,
    AppSettingsService settings,
    GuestService guests,
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
                    var error = await SendAsync(u.ReminderUrl!, u.ReminderToken, title, message!, link, allowPrivate: u.IsAdmin);
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

    /// <summary>
    /// Posts a reminder. Only admins may target addresses inside the network (a self-hosted ntfy);
    /// for everyone else the server refuses private, loopback and link-local addresses, checked when
    /// connecting so a public name that resolves to a home address is caught too.
    /// </summary>
    public async Task<string?> SendAsync(string url, string? token, string title, string message, string? link, bool allowPrivate)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "Not an http(s) URL.";
        try
        {
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (ctx, ct) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
                    var target = addresses.FirstOrDefault(a => allowPrivate || IsPublic(a))
                        ?? throw new HttpRequestException("That address is inside the network; only an admin can send reminders there.");
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(target, ctx.DnsEndPoint.Port), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(message, Encoding.UTF8, "text/plain") };
            if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("Title", "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(title)) + "?=");
            req.Headers.TryAddWithoutValidation("Tags", "books");
            if (link is not null) req.Headers.TryAddWithoutValidation("Click", link);
            using var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return null;
            return allowPrivate ? $"{(int)resp.StatusCode} {resp.ReasonPhrase}" : "The server refused the reminder.";
        }
        catch (Exception ex)
        {
            logger.LogInformation("Reminder to {Host} failed: {Error}", uri.Host, ex.Message);
            // Details only for admins, so the reminder field can't be used to map the network.
            return allowPrivate || ex.Message.StartsWith("That address is inside") ? ex.Message : "Couldn't reach that address.";
        }
    }

    /// <summary>False for loopback, private (RFC 1918 / ULA), link-local, CGNAT and other non-internet addresses.</summary>
    public static bool IsPublic(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any) || a.Equals(IPAddress.Broadcast)) return false;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 0 || b[0] >= 224
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // CGNAT, also Tailscale
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19)));
        }
        return !(a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || (a.GetAddressBytes()[0] & 0xfe) == 0xfc);
    }
}
