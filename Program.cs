using System.IO.Compression;
using System.Net;
using System.Security.Claims;
using System.Text;
using CourseHub.Components;
using CourseHub.Data;
using CourseHub.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var hubOptions = builder.Configuration.GetSection("CourseHub").Get<CourseHubOptions>() ?? new CourseHubOptions();
var toolOptions = builder.Configuration.GetSection("MediaTools").Get<MediaToolsOptions>() ?? new MediaToolsOptions();
builder.Services.AddSingleton(hubOptions);
builder.Services.AddSingleton(toolOptions);
builder.Services.AddSingleton<CourseLibrary>();
builder.Services.AddSingleton<ProgressService>();
builder.Services.AddSingleton<UserDataService>();
builder.Services.AddSingleton<StatsService>();
builder.Services.AddSingleton<UserService>();
builder.Services.AddSingleton<MediaTools>();
builder.Services.AddSingleton<CodeBrowser>();
builder.Services.AddSingleton<TranscriptService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TranscriptService>());
builder.Services.AddHttpClient();

// ---------- database ----------

var connectionString = builder.Configuration.GetConnectionString("Progress") ?? "Data Source=coursehub.db";
var dbFile = new SqliteConnectionStringBuilder(connectionString).DataSource;
if (Path.GetDirectoryName(Path.GetFullPath(dbFile)) is { } dbDir) Directory.CreateDirectory(dbDir);
builder.Services.AddDbContextFactory<ProgressDb>(o => o.UseSqlite(connectionString));

// ---------- auth ----------

// Keys encrypt the sign-in cookie. Persisting them means restarts/upgrades don't sign everyone out.
var keysDir = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, hubOptions.KeysFolder));
builder.Services.AddDataProtection().SetApplicationName("CourseHub").PersistKeysToFileSystem(Directory.CreateDirectory(keysDir));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
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
        // API/media requests get a 401 instead of an HTML redirect.
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
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy("Admin", p => p.RequireClaim(Claims.Admin, "true"));
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

if (hubOptions.TrustForwardedHeaders) app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous();

app.MapGet("/health", (CourseLibrary lib) => Results.Ok(new { status = "ok", courses = lib.Courses.Count, lastScan = lib.LastScan }))
    .AllowAnonymous();

app.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).AllowAnonymous().DisableAntiforgery();

// ---------- media ----------

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".mkv"] = "video/x-matroska";
contentTypes.Mappings[".m4v"] = "video/mp4";
contentTypes.Mappings[".mov"] = "video/mp4"; // most .mov files are H.264 and play fine when labelled mp4

// Streams lesson files with HTTP range support so videos can seek.
app.MapGet("/media/{id}", (string id, CourseLibrary lib) =>
{
    var lesson = lib.GetLesson(id);
    if (lesson is null || !File.Exists(lesson.FullPath)) return Results.NotFound();
    if (!contentTypes.TryGetContentType(lesson.FullPath, out var type)) type = "application/octet-stream";
    return TypedResults.PhysicalFile(lesson.FullPath, type, enableRangeProcessing: true);
});

app.MapGet("/thumb/{id}", async (string id, CourseLibrary lib, MediaTools tools, HttpContext ctx) =>
{
    var lesson = lib.GetLesson(id);
    if (lesson is null) return Results.NotFound();
    var file = await tools.ThumbnailAsync(lesson);
    if (file is null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "private, max-age=86400";
    return TypedResults.PhysicalFile(file, "image/jpeg");
});

// Course cover: course.json "cover" / cover.jpg in the folder, else the first video's thumbnail.
app.MapGet("/cover/{courseId}", async (string courseId, CourseLibrary lib, MediaTools tools, HttpContext ctx) =>
{
    var course = lib.GetCourse(courseId);
    if (course is null) return Results.NotFound();
    if (course.CoverPath is not null)
    {
        contentTypes.TryGetContentType(course.CoverPath, out var type);
        ctx.Response.Headers.CacheControl = "private, max-age=3600";
        return TypedResults.PhysicalFile(course.CoverPath, type ?? "image/jpeg");
    }
    var first = course.AllLessons.FirstOrDefault(l => l.Kind == LessonKind.Video);
    var file = first is null ? null : await tools.ThumbnailAsync(first);
    if (file is null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "private, max-age=3600";
    return TypedResults.PhysicalFile(file, "image/jpeg");
});

app.MapGet("/subs/{id}.vtt", (string id, CourseLibrary lib, TranscriptService transcripts) =>
{
    var lesson = lib.GetLesson(id);
    var vtt = lesson is null ? null : transcripts.ToVtt(lesson);
    return vtt is null ? Results.NotFound() : Results.Text(vtt, "text/vtt", Encoding.UTF8);
});

// ---------- export (the signed-in user's own data) ----------

app.MapGet("/api/export", async (ClaimsPrincipal user, UserDataService data) =>
    Results.File(await data.ExportAsync(user.GetUserId()!.Value), "application/json", $"coursehub-backup-{user.Identity?.Name}-{DateTime.Now:yyyy-MM-dd}.json"));

app.MapGet("/api/notes.md", async (string? course, ClaimsPrincipal user, CourseLibrary lib, UserDataService data) =>
{
    var c = course is null ? null : lib.GetCourse(course);
    var md = await data.NotesMarkdownAsync(user.GetUserId()!.Value, c);
    var name = c is null ? "coursehub-notes" : $"{c.Title} notes";
    return Results.File(Encoding.UTF8.GetBytes(md), "text/markdown", $"{name}.md");
});

// ---------- code resources ----------

app.MapGet("/code/{resourceId}/raw", (string resourceId, string path, CourseLibrary lib) =>
{
    if (lib.GetResource(resourceId) is not { } r) return Results.NotFound();
    var full = CodeBrowser.Resolve(r.Folder, path);
    return full is null ? Results.NotFound() : TypedResults.PhysicalFile(full, "application/octet-stream", Path.GetFileName(full));
});

// Whole resource folder as a zip (skips bin/obj/.git and friends), streamed without a temp file.
app.MapGet("/code/{resourceId}/zip", async (string resourceId, CourseLibrary lib, CodeBrowser code, HttpContext ctx) =>
{
    if (lib.GetResource(resourceId) is not { } r) { ctx.Response.StatusCode = 404; return; }
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

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

static bool IsApiRequest(HttpRequest r) =>
    r.Path.StartsWithSegments("/api") || r.Path.StartsWithSegments("/media") || r.Path.StartsWithSegments("/thumb")
    || r.Path.StartsWithSegments("/cover") || r.Path.StartsWithSegments("/subs") || r.Path.StartsWithSegments("/code")
       && (r.Path.Value?.EndsWith("/raw") == true || r.Path.Value?.EndsWith("/zip") == true);
