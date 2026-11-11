namespace Lyceum.Services;

/// <summary>
/// The URL prefix Lyceum is served under, e.g. "/courses" for https://example.com/courses/.
/// Empty when it has its own (sub)domain. Set once at startup from Lyceum__BasePath.
/// </summary>
public static class AppPaths
{
    public static string Base { get; private set; } = "";

    public static void Configure(string? basePath)
    {
        var b = (basePath ?? "").Trim().Trim('/');
        Base = b.Length == 0 ? "" : "/" + b;
    }

    /// <summary>Absolute path including the base, for URLs that leave the page (API responses, redirects, media).</summary>
    public static string Url(string path) => Base + (path.StartsWith('/') ? path : "/" + path);
}
