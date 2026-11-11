using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Lyceum.Services;

/// <summary>
/// Signed, short-lived media URLs for devices that can't send the sign-in cookie (Chromecast).
/// A token is bound to one user and one lesson and only opens that lesson's video, subtitles and images.
/// </summary>
public sealed class CastTokenService(IDataProtectionProvider dataProtection, UserService users)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);
    private readonly ITimeLimitedDataProtector _protector = dataProtection.CreateProtector("CourseHub.Cast.v1").ToTimeLimitedDataProtector();  // Former app name kept on purpose: it is part of the encryption key for data already stored.

    /// <summary>The token carries the user's sign-in stamp, so signing out everywhere (or a guest link expiring) also ends casting.</summary>
    public async Task<string?> CreateAsync(int userId, Lesson lesson, Course course) =>
        await users.CurrentStampAsync(userId) is { } stamp ? _protector.Protect($"{userId}|{lesson.Id}|{course.Id}|{stamp}", Lifetime) : null;

    public (int UserId, string LessonId, string CourseId, string Stamp)? Read(string token)
    {
        try
        {
            var p = _protector.Unprotect(token).Split('|');
            return p.Length == 4 ? (int.Parse(p[0]), p[1], p[2], p[3]) : null;
        }
        catch
        {
            return null;
        }
    }
}

public sealed class CastTokenAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    CastTokenService tokens,
    UserService users) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "CastToken";

    public static bool Has(HttpRequest r) => r.Query.ContainsKey("cast");

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Query.TryGetValue("cast", out var raw)) return AuthenticateResult.NoResult();
        if (tokens.Read(raw.ToString()) is not { } t) return AuthenticateResult.Fail("Invalid or expired cast link.");

        // Only this lesson's media: /media/{id}, /subs/{id}.vtt, /thumb/{id}, /cover/{courseId}.
        var path = Request.Path.Value ?? "";
        var allowed = path == $"/media/{t.LessonId}" || path == $"/subs/{t.LessonId}.vtt" || path == $"/thumb/{t.LessonId}" || path == $"/cover/{t.CourseId}";
        if (!allowed) return AuthenticateResult.Fail("This cast link is for a different file.");

        if (!await users.IsSessionValidAsync(t.UserId, t.Stamp)) return AuthenticateResult.Fail("This cast link is no longer valid.");
        var user = await users.GetAsync(t.UserId);
        if (user is null) return AuthenticateResult.Fail("User not found.");
        // The Chromecast receiver loads subtitles with CORS.
        Response.Headers.AccessControlAllowOrigin = "*";
        return AuthenticateResult.Success(new AuthenticationTicket(UserService.PrincipalFor(user, "", SchemeName), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
