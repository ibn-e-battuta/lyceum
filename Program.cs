using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Text;
using CourseHub.Api;
using CourseHub.Components;
using CourseHub.Data;
using CourseHub.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);
AppPaths.Configure(builder.Configuration["CourseHub:BasePath"]);

var hubOptions = builder.Configuration.GetSection("CourseHub").Get<CourseHubOptions>() ?? new CourseHubOptions();
var toolOptions = builder.Configuration.GetSection("MediaTools").Get<MediaToolsOptions>() ?? new MediaToolsOptions();
var oidc = builder.Configuration.GetSection("Oidc").Get<OidcOptions>() ?? new OidcOptions();
builder.Services.AddSingleton(hubOptions);
builder.Services.AddSingleton(toolOptions);
builder.Services.AddSingleton(oidc);
builder.Services.AddSingleton<AppSettingsService>();
builder.Services.AddSingleton<MediaTools>();
builder.Services.AddSingleton<MediaProbe>();
builder.Services.AddSingleton<CourseLibrary>();
builder.Services.AddSingleton<ProgressService>();
builder.Services.AddSingleton<UserDataService>();
builder.Services.AddSingleton<StatsService>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<ActivityService>();
builder.Services.AddSingleton<ApiKeyService>();
builder.Services.AddSingleton<PresenceService>();
builder.Services.AddSingleton<CodeBrowser>();
builder.Services.AddSingleton<AiService>();
builder.Services.AddSingleton<Localizer>();
builder.Services.AddSingleton<TranscriptService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TranscriptService>());
builder.Services.AddSingleton<Transcoder>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Transcoder>());
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton<WatchTimeService>();
builder.Services.AddSingleton<GuestService>();
builder.Services.AddSingleton<ReviewService>();
builder.Services.AddSingleton<CommentService>();
builder.Services.AddSingleton<CastTokenService>();
builder.Services.AddSingleton<HealthReportService>();
builder.Services.AddSingleton<MetricsService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
builder.Services.AddSingleton<ReminderService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReminderService>());
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NotificationService>());
builder.Services.AddHttpClient();
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "CourseHub API";
    doc.Info.Description = "Authenticate with `Authorization: Bearer <API key>` (create keys in Settings → API keys).";
    return Task.CompletedTask;
}));

// ---------- database ----------

var connectionString = builder.Configuration.GetConnectionString("Progress") ?? "Data Source=coursehub.db";
var dbFile = new SqliteConnectionStringBuilder(connectionString).DataSource;
if (Path.GetDirectoryName(Path.GetFullPath(dbFile)) is { } dbDir) Directory.CreateDirectory(dbDir);
builder.Services.AddDbContextFactory<ProgressDb>(o => o.UseSqlite(connectionString));

// ---------- auth ----------

// Keys encrypt the sign-in cookie. Persisting them means restarts/upgrades don't sign everyone out.
var keysDir = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, hubOptions.KeysFolder));
builder.Services.AddDataProtection().SetApplicationName("CourseHub").PersistKeysToFileSystem(Directory.CreateDirectory(keysDir));

const string SmartScheme = "CookieOrApiKey";
const string ExternalScheme = "External";
var auth = builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = SmartScheme;
        o.DefaultChallengeScheme = SmartScheme;
    })
    // Requests with an API key use it; everything else uses the browser cookie.
    .AddPolicyScheme(SmartScheme, "Cookie or API key", o =>
        o.ForwardDefaultSelector = ctx =>
            CastTokenAuthHandler.Has(ctx.Request) ? CastTokenAuthHandler.SchemeName
            : ApiKeyAuthHandler.KeyFrom(ctx.Request) is not null ? ApiKeyAuthHandler.SchemeName
            : CookieAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, null)
    .AddScheme<AuthenticationSchemeOptions, CastTokenAuthHandler>(CastTokenAuthHandler.SchemeName, null)
    .AddCookie(o =>
    {
        o.Cookie.Name = "coursehub.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/";
        o.ExpireTimeSpan = TimeSpan.FromDays(60);
        o.SlidingExpiration = true;
        o.Events.OnValidatePrincipal = ctx => ctx.HttpContext.RequestServices.GetRequiredService<UserService>().ValidatePrincipalAsync(ctx);
        // API/media requests get status codes instead of HTML redirects.
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (IsApiRequest(ctx.Request)) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            if (IsApiRequest(ctx.Request)) ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            else ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    });

if (oidc.Enabled)
{
    // Single sign-on: the provider signs into a short-lived "External" cookie, then /auth/oidc/complete
    // links it to a CourseHub account and issues the normal cookie.
    auth.AddCookie(ExternalScheme, o =>
    {
        o.Cookie.Name = "coursehub.external";
        o.ExpireTimeSpan = TimeSpan.FromMinutes(10);
    });
    auth.AddOpenIdConnect(OidcOptions.Scheme, oidc.DisplayName, o =>
    {
        o.SignInScheme = ExternalScheme;
        o.Authority = oidc.Authority;
        o.ClientId = oidc.ClientId;
        o.ClientSecret = oidc.ClientSecret;
        o.ResponseType = OpenIdConnectResponseType.Code;
        // Query-mode callback + Lax cookies: works over plain http on a LAN as well as behind https
        // (the default form_post needs SameSite=None cookies, which browsers reject without https).
        o.ResponseMode = OpenIdConnectResponseMode.Query;
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.NonceCookie.SameSite = SameSiteMode.Lax;
        o.UsePkce = true;
        o.SaveTokens = false;
        o.GetClaimsFromUserInfoEndpoint = true;
        o.MapInboundClaims = false;
        o.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
        o.CallbackPath = "/signin-oidc";
        o.Scope.Clear();
        foreach (var scope in oidc.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)) o.Scope.Add(scope);
        o.ClaimActions.MapAll(); // keep groups and other custom claims from the userinfo endpoint
        o.Events.OnRemoteFailure = ctx =>
        {
            ctx.Response.Redirect(AppPaths.Base + "/login?error=" + Uri.EscapeDataString("Single sign-on failed: " + ctx.Failure?.Message));
            ctx.HandleResponse();
            return Task.CompletedTask;
        };
    });
}

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(SmartScheme).RequireAuthenticatedUser().Build())
    .AddPolicy("Admin", p => p.AddAuthenticationSchemes(SmartScheme).RequireClaim(Claims.Admin, "true"));
builder.Services.AddCascadingAuthenticationState();

if (hubOptions.TrustForwardedHeaders)
{
    // Behind a reverse proxy (Nginx, Caddy, Traefik…): use the client IP / scheme it forwards.
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

// ---------- startup ----------

await using (var db = await app.Services.GetRequiredService<IDbContextFactory<ProgressDb>>().CreateDbContextAsync())
{
    await db.MigrateWithLegacyImportAsync(app.Logger);
}
await app.Services.GetRequiredService<UserService>().BootstrapAdminAsync(
    builder.Configuration["COURSEHUB_ADMIN_USER"], builder.Configuration["COURSEHUB_ADMIN_PASSWORD"]);

var library = app.Services.GetRequiredService<CourseLibrary>();
await library.RescanAsync();
library.StartWatching();

var presence = app.Services.GetRequiredService<PresenceService>();
app.Services.GetRequiredService<ProgressService>().Watched += (userId, course, lesson) => presence.Track(userId, course, lesson);
app.Services.GetRequiredService<NotificationService>().Subscribe(
    library, app.Services.GetRequiredService<ActivityService>(), app.Services.GetRequiredService<TranscriptService>());

if (hubOptions.TrustForwardedHeaders) app.UseForwardedHeaders();
// Sub-path hosting (https://example.com/courses): requests arrive with the prefix; strip it so routing works.
if (AppPaths.Base.Length > 0)
{
    app.UsePathBase(AppPaths.Base);
    // Someone opening the bare host (http://server:8080/) lands on the app instead of a login page that can't load.
    app.Use((ctx, next) =>
    {
        if (ctx.Request.PathBase.HasValue || ctx.Request.Path != "/") return next(ctx);
        ctx.Response.Redirect(AppPaths.Base + "/");
        return Task.CompletedTask;
    });
}
app.UseRouting(); // explicitly after UsePathBase, so routes match without the prefix
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseRequestLocalization(o =>
{
    var cultures = Localizer.Languages.Keys.Select(c => new CultureInfo(c)).ToList();
    o.DefaultRequestCulture = new RequestCulture("en");
    o.SupportedCultures = cultures;
    o.SupportedUICultures = cultures;
    o.RequestCultureProviders = [new CookieRequestCultureProvider(), new AcceptLanguageHeaderRequestCultureProvider()];
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous();
app.MapOpenApi().AllowAnonymous();

// Guest links: sign in as the link's guest account and go straight to the course.
app.MapGet("/guest/{token}", async (string token, HttpContext ctx, GuestService guests, CourseLibrary lib) =>
{
    var (guest, link) = await guests.RedeemAsync(token);
    if (guest is null || link is null)
        return Results.Redirect(AppPaths.Base + "/login?error=" + Uri.EscapeDataString(link is null ? "This guest link doesn't exist." : "This guest link has expired."));
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserService.Principal(guest),
        new AuthenticationProperties { IsPersistent = true, ExpiresUtc = link.ExpiresAt });
    var course = lib.GetCourseByFolder(link.CoursePath);
    return Results.Redirect(AppPaths.Base + (course is null ? "/" : $"/course/{course.Id}"));
}).AllowAnonymous();

// Prometheus: off unless CourseHub__Metrics__Enabled=true; if CourseHub__Metrics__Token is set, scrapers must send it as a bearer token.
var metricsEnabled = builder.Configuration.GetValue<bool>("CourseHub:Metrics:Enabled");
var metricsToken = builder.Configuration["CourseHub:Metrics:Token"];
if (metricsEnabled)
{
    app.MapGet("/metrics", async (HttpContext ctx, MetricsService metrics) =>
    {
        if (!string.IsNullOrEmpty(metricsToken) && ctx.Request.Headers.Authorization.ToString() != $"Bearer {metricsToken}")
            return Results.Unauthorized();
        return Results.Text(await metrics.RenderAsync(), "text/plain; version=0.0.4; charset=utf-8");
    }).AllowAnonymous();
}

app.MapGet("/api/admin/backups/{name}", (string name, BackupService backups) =>
    backups.PathOf(name) is { } p ? TypedResults.PhysicalFile(p, "application/zip", name) : Results.NotFound())
    .RequireAuthorization("Admin");

app.MapGet("/health", (CourseLibrary lib) => Results.Ok(new { status = "ok", courses = lib.Courses.Count, lastScan = lib.LastScan }))
    .AllowAnonymous();

app.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect(AppPaths.Base + "/login");
}).AllowAnonymous().DisableAntiforgery();

// Language switcher: stores the choice in the standard ASP.NET Core culture cookie and reloads.
app.MapGet("/culture", (string culture, string? redirect, HttpContext ctx) =>
{
    if (Localizer.Languages.ContainsKey(culture))
        ctx.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax });
    return Results.LocalRedirect(redirect is { Length: > 0 } r && r.StartsWith('/') && !r.StartsWith("//") ? r : AppPaths.Base + "/");
}).AllowAnonymous();

if (oidc.Enabled)
{
    app.MapGet("/auth/oidc/login", (string? returnUrl) =>
        Results.Challenge(new AuthenticationProperties { RedirectUri = AppPaths.Base + "/auth/oidc/complete?returnUrl=" + Uri.EscapeDataString(LocalUrl(returnUrl)) },
            [OidcOptions.Scheme])).AllowAnonymous();

    app.MapGet("/auth/oidc/complete", async (string? returnUrl, HttpContext ctx, UserService users) =>
    {
        var result = await ctx.AuthenticateAsync(ExternalScheme);
        if (!result.Succeeded || result.Principal is null) return Results.Redirect(AppPaths.Base + "/login?error=" + Uri.EscapeDataString("Single sign-on didn't complete. Try again."));
        var p = result.Principal;
        var subject = p.FindFirstValue("sub") ?? p.FindFirstValue(ClaimTypes.NameIdentifier);
        if (subject is null) return Results.Redirect(AppPaths.Base + "/login?error=" + Uri.EscapeDataString("The identity provider didn't send a user id (sub)."));
        var groups = p.FindAll(oidc.GroupsClaim).SelectMany(c => c.Value.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(g => g.Trim('"')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isAdmin = !string.IsNullOrWhiteSpace(oidc.AdminGroup) && groups.Contains(oidc.AdminGroup);
        var (user, error) = await users.ExternalSignInAsync(OidcOptions.Scheme, subject,
            p.FindFirstValue(oidc.UsernameClaim) ?? p.FindFirstValue("email"), p.FindFirstValue("name"),
            isAdmin, oidc.AutoCreateUsers, oidc.MatchExistingByUsername, syncAdmin: !string.IsNullOrWhiteSpace(oidc.AdminGroup));
        await ctx.SignOutAsync(ExternalScheme);
        if (user is null) return Results.Redirect(AppPaths.Base + "/login?error=" + Uri.EscapeDataString(error ?? "Sign-in failed."));
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserService.Principal(user),
            new AuthenticationProperties { IsPersistent = true });
        return Results.LocalRedirect(LocalUrl(returnUrl));
    }).AllowAnonymous();
}

// ---------- media (every file endpoint checks the user's library access) ----------

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".mkv"] = "video/x-matroska";
contentTypes.Mappings[".m4v"] = "video/mp4";
contentTypes.Mappings[".mov"] = "video/mp4"; // most .mov files are H.264 and play fine when labelled mp4

async Task<Course?> AccessibleCourse(ClaimsPrincipal user, Course? course, UserService users) =>
    course is not null && user.GetUserId() is { } id && CourseLibrary.CanAccess(course, await users.ScopeAsync(id)) ? course : null;

// Streams lesson files with HTTP range support so videos can seek. ?v=remux|h264 serves a converted copy.
app.MapGet("/media/{id}", async (string id, string? v, HttpContext ctx, ClaimsPrincipal user, CourseLibrary lib, UserService users, Transcoder transcoder, WatchTimeService watch) =>
{
    var lesson = lib.GetLesson(id);
    if (lesson is null || await AccessibleCourse(user, lib.CourseOf(lesson), users) is null || !File.Exists(lesson.FullPath)) return Results.NotFound();
    // Kids' daily limit is enforced here too, so it can't be bypassed by opening the file directly.
    var viewer = await users.GetAsync(user.GetUserId()!.Value);
    if (viewer is { IsKid: true } kid && await watch.RemainingAsync(kid) is <= 0)
        return Results.Problem("Time's up for today.", statusCode: StatusCodes.Status403Forbidden);
    // Downloads would play without these checks, so kids' and guest accounts can't make them.
    if (viewer is { IsKid: true } or { IsGuest: true } && ctx.Request.Headers.ContainsKey("X-CourseHub-Offline"))
        return Results.Problem("This account can't save lessons offline.", statusCode: StatusCodes.Status403Forbidden);
    if (v is "remux" or "h264" or "audio")
    {
        var variant = v switch { "remux" => PlaybackVariant.Remux, "audio" => PlaybackVariant.Audio, _ => PlaybackVariant.H264 };
        var prepared = transcoder.PreparedFile(lesson, variant);
        if (prepared is null) return Results.NotFound(new { error = "Not prepared yet." });
        transcoder.Touch(prepared);
        return TypedResults.PhysicalFile(prepared, variant == PlaybackVariant.Audio ? "audio/mp4" : "video/mp4", enableRangeProcessing: true);
    }
    if (!contentTypes.TryGetContentType(lesson.FullPath, out var type)) type = "application/octet-stream";
    return TypedResults.PhysicalFile(lesson.FullPath, type, enableRangeProcessing: true);
});

// The Downloads page asks this when online, and clears its downloads for accounts that may not keep any.
app.MapGet("/api/offline/policy", async (ClaimsPrincipal user, UserService users) =>
    Results.Ok(new { allowed = await users.GetAsync(user.GetUserId()!.Value) is not ({ IsKid: true } or { IsGuest: true }) }));

app.MapPost("/api/media/{id}/prepare", async (string id, string variant, ClaimsPrincipal user, CourseLibrary lib, UserService users, Transcoder transcoder) =>
{
    var lesson = lib.GetLesson(id);
    if (lesson is null || await AccessibleCourse(user, lib.CourseOf(lesson), users) is null) return Results.NotFound();
    var v = variant switch { "remux" => PlaybackVariant.Remux, "audio" => PlaybackVariant.Audio, _ => PlaybackVariant.H264 };
    transcoder.Enqueue(lesson, v);
    var job = transcoder.JobFor(lesson, v);
    return Results.Ok(new { ready = transcoder.PreparedFile(lesson, v) is not null, state = job?.State.ToString(), percent = job?.Percent });
}).DisableAntiforgery();

app.MapGet("/attachment/{id}", async (string id, bool? download, ClaimsPrincipal user, CourseLibrary lib, UserService users) =>
{
    if (lib.GetAttachment(id) is not { } a || await AccessibleCourse(user, a.Course, users) is null || !File.Exists(a.File.FullPath)) return Results.NotFound();
    if (!contentTypes.TryGetContentType(a.File.FullPath, out var type)) type = "application/octet-stream";
    // PDFs/images open in the browser; everything else downloads.
    return a.File.OpensInBrowser && download != true
        ? TypedResults.PhysicalFile(a.File.FullPath, type, enableRangeProcessing: true)
        : TypedResults.PhysicalFile(a.File.FullPath, type, a.File.Name, enableRangeProcessing: true);
});

app.MapGet("/thumb/{id}", async (string id, ClaimsPrincipal user, CourseLibrary lib, UserService users, MediaTools tools, HttpContext ctx) =>
{
    var lesson = lib.GetLesson(id);
    if (lesson is null || await AccessibleCourse(user, lib.CourseOf(lesson), users) is null) return Results.NotFound();
    var file = await tools.ThumbnailAsync(lesson);
    if (file is null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "private, max-age=86400";
    return TypedResults.PhysicalFile(file, "image/jpeg");
});

// Course cover: course.json "cover" / cover.jpg in the folder, else the first video's thumbnail.
app.MapGet("/cover/{courseId}", async (string courseId, ClaimsPrincipal user, CourseLibrary lib, UserService users, MediaTools tools, HttpContext ctx) =>
{
    var course = await AccessibleCourse(user, lib.GetCourse(courseId), users);
    if (course is null) return Results.NotFound();
    // The URL carries ?v=<file time>, so the image can be cached for a long time and still update when it changes.
    var cache = ctx.Request.Query.ContainsKey("v") ? "private, max-age=2592000, immutable" : "private, max-age=3600";
    if (course.CoverPath is not null)
    {
        contentTypes.TryGetContentType(course.CoverPath, out var type);
        ctx.Response.Headers.CacheControl = cache;
        return TypedResults.PhysicalFile(course.CoverPath, type ?? "image/jpeg");
    }
    var first = course.AllLessons.FirstOrDefault(l => l.Kind == LessonKind.Video);
    var file = first is null ? null : await tools.ThumbnailAsync(first);
    if (file is null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = cache;
    return TypedResults.PhysicalFile(file, "image/jpeg");
});

app.MapGet("/subs/{id}.vtt", async (string id, string? lang, ClaimsPrincipal user, CourseLibrary lib, UserService users, TranscriptService transcripts) =>
{
    var lesson = lib.GetLesson(id);
    if (lesson is null || await AccessibleCourse(user, lib.CourseOf(lesson), users) is null) return Results.NotFound();
    var vtt = transcripts.ToVtt(lesson, lang);
    return vtt is null ? Results.NotFound() : Results.Text(vtt, "text/vtt", Encoding.UTF8);
});

// ---------- export (the signed-in user's own data) ----------

app.MapGet("/api/export", async (ClaimsPrincipal user, UserDataService data) =>
    Results.File(await data.ExportAsync(user.GetUserId()!.Value), "application/json", $"coursehub-backup-{user.Identity?.Name}-{DateTime.Now:yyyy-MM-dd}.json"));

app.MapGet("/api/notes.md", async (string? course, ClaimsPrincipal user, CourseLibrary lib, UserService users, UserDataService data) =>
{
    var c = course is null ? null : await AccessibleCourse(user, lib.GetCourse(course), users);
    var md = await data.NotesMarkdownAsync(user.GetUserId()!.Value, c);
    var name = c is null ? "coursehub-notes" : $"{c.Title} notes";
    return Results.File(Encoding.UTF8.GetBytes(md), "text/markdown", $"{name}.md");
});

app.MapGet("/api/flashcards/{lessonId}.txt", async (string lessonId, ClaimsPrincipal user, CourseLibrary lib, UserService users, AiService ai) =>
{
    var lesson = lib.GetLesson(lessonId);
    if (lesson is null || await AccessibleCourse(user, lib.CourseOf(lesson), users) is null) return Results.NotFound();
    var cards = await ai.CachedFlashcardsAsync(lesson);
    return cards is null ? Results.NotFound() : Results.File(Encoding.UTF8.GetBytes(AiService.ToAnki(cards)), "text/plain", $"{lesson.Title} flashcards.txt");
});

// ---------- code resources ----------

app.MapGet("/code/{resourceId}/raw", async (string resourceId, string path, ClaimsPrincipal user, CourseLibrary lib, UserService users) =>
{
    if (lib.GetResource(resourceId) is not { } r || await AccessibleCourse(user, r.Course, users) is null) return Results.NotFound();
    var full = CodeBrowser.Resolve(r.Folder, path);
    return full is null ? Results.NotFound() : TypedResults.PhysicalFile(full, "application/octet-stream", Path.GetFileName(full));
});

// Whole resource folder as a zip (skips bin/obj/.git and friends), streamed without a temp file.
app.MapGet("/code/{resourceId}/zip", async (string resourceId, ClaimsPrincipal user, CourseLibrary lib, UserService users, CodeBrowser code, HttpContext ctx) =>
{
    if (lib.GetResource(resourceId) is not { } r || await AccessibleCourse(user, r.Course, users) is null) { ctx.Response.StatusCode = 404; return; }
    var name = string.Concat(r.Folder.Name.Split(Path.GetInvalidFileNameChars())).Replace(" / ", "-");
    ctx.Response.ContentType = "application/zip";
    ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{name}.zip\"";
    await using var zip = new ZipArchive(ctx.Response.BodyWriter.AsStream(), ZipArchiveMode.Create, leaveOpen: true);
    foreach (var f in code.ListFiles(r.Folder))
    {
        var full = CodeBrowser.Resolve(r.Folder, f.RelativePath);
        if (full is null) continue;
        var entry = zip.CreateEntry(Path.Combine(name, f.RelativePath).Replace('\\', '/'), CompressionLevel.Fastest);
        await using var dst = entry.Open();
        await using var src = File.OpenRead(full);
        await src.CopyToAsync(dst, ctx.RequestAborted);
    }
});

// Opening VS Code/Rider/Finder: admins only, only from the machine running CourseHub, and only from our own
// pages (the custom header can't be sent cross-site without a CORS preflight).
app.MapPost("/api/open/{with}/{resourceId}", async (string with, string resourceId, string? path, HttpContext ctx, CourseLibrary lib, CodeBrowser code) =>
{
    var remote = ctx.Connection.RemoteIpAddress;
    if (remote is null || !IPAddress.IsLoopback(remote) || hubOptions.TrustForwardedHeaders)
        return Results.Problem("Only available on the computer running CourseHub.", statusCode: 403);
    if (!ctx.Request.Headers.ContainsKey("X-CourseHub")) return Results.BadRequest();
    if (with is not ("vscode" or "rider" or "finder")) return Results.NotFound();
    if (lib.GetResource(resourceId) is not { } r) return Results.NotFound();
    var target = string.IsNullOrEmpty(path) ? r.Folder.FullPath : CodeBrowser.Resolve(r.Folder, path);
    if (target is null) return Results.NotFound();
    var error = await code.OpenAsync(target, with);
    return error is null ? Results.Ok() : Results.Problem(error, statusCode: 500);
}).RequireAuthorization("Admin").DisableAntiforgery();

app.MapCourseHubApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

static string LocalUrl(string? url) => url is { Length: > 0 } u && u.StartsWith('/') && !u.StartsWith("//") && !u.StartsWith("/\\") ? u : AppPaths.Base + "/";

static bool IsApiRequest(HttpRequest r) =>
    r.Path.StartsWithSegments("/api") || r.Path.StartsWithSegments("/media") || r.Path.StartsWithSegments("/thumb")
    || r.Path.StartsWithSegments("/cover") || r.Path.StartsWithSegments("/subs") || r.Path.StartsWithSegments("/attachment")
    || r.Path.StartsWithSegments("/code") && (r.Path.Value?.EndsWith("/raw") == true || r.Path.Value?.EndsWith("/zip") == true);

/// <summary>OpenID Connect single sign-on (Authentik, Authelia, Keycloak, Zitadel, Pocket ID…). Configure with Oidc__* env vars.</summary>
public sealed class OidcOptions
{
    public const string Scheme = "oidc";
    public string? Authority { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string DisplayName { get; set; } = "Single sign-on";
    public string Scopes { get; set; } = "openid profile email groups";
    public string UsernameClaim { get; set; } = "preferred_username";
    public string GroupsClaim { get; set; } = "groups";
    /// <summary>Members of this group become admins (and stop being admins when removed from it).</summary>
    public string? AdminGroup { get; set; }
    /// <summary>Create a CourseHub account the first time someone signs in.</summary>
    public bool AutoCreateUsers { get; set; } = true;
    /// <summary>Link to an existing local account with the same username (only enable if your IdP usernames are trusted).</summary>
    public bool MatchExistingByUsername { get; set; }
    /// <summary>Hide the username/password form.</summary>
    public bool DisablePasswordLogin { get; set; }
    public bool RequireHttpsMetadata { get; set; } = true;
    public bool Enabled => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}
