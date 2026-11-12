using System.Net;
using System.Text;
using Markdig;

namespace Lyceum.Services;

/// <summary>
/// Builds what the reading-lesson frame shows for HTML and Markdown pages: Markdown rendered as a styled page,
/// a small script that opens outside links in a new tab, and a stub that hands links to other lessons to the player.
/// </summary>
public static partial class LessonPages
{
    // Raw HTML is off: these files come from course downloads, and the page shares Lyceum's origin.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    /// <summary>Opens links to other sites in a new tab instead of inside the lesson frame.</summary>
    public const string LinkScript = """
        <script>
        document.addEventListener("click", function (e) {
          var a = e.target.closest && e.target.closest("a[href]");
          if (a && a.origin !== location.origin) { a.target = "_blank"; a.rel = "noopener"; }
        }, true);
        </script>
        """;

    public static string Markdown(string markdown, string title)
    {
        // Lectern (and other tools) start files with an HTML comment such as "<!-- source: … -->".
        markdown = LeadingComment().Replace(markdown, "");
        var body = Markdig.Markdown.ToHtml(markdown, Pipeline);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>");
        sb.Append("<script>try{var t=localStorage.getItem('lyceum.theme');if(t==='light'||t==='dark')document.documentElement.dataset.theme=t}catch(e){}</script>");
        sb.Append("<style>").Append(Css).Append("</style>");
        sb.Append("<link rel=\"stylesheet\" href=\"https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/styles/github-dark.min.css\">");
        sb.Append("</head><body><article class=\"md\">").Append(body).Append("</article>");
        sb.Append("<script src=\"https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.9.0/highlight.min.js\"></script>");
        sb.Append("<script>window.hljs&&document.querySelectorAll('pre code').forEach(function(c){hljs.highlightElement(c)})</script>");
        sb.Append(LinkScript).Append("</body></html>");
        return sb.ToString();
    }

    /// <summary>Shown for a moment when a page links to another lesson: asks the player to open that lesson.</summary>
    public static string OpenLesson(string lessonId) =>
        $"<!doctype html><meta charset=\"utf-8\"><script>parent.postMessage({{ lyceumLesson: \"{lessonId}\" }}, location.origin)</script>";

    [System.Text.RegularExpressions.GeneratedRegex(@"^\uFEFF?\s*<!--[\s\S]*?-->\s*")]
    private static partial System.Text.RegularExpressions.Regex LeadingComment();

    private const string Css = """
        :root { --bg:#0f1115; --text:#e9ecf2; --muted:#9aa3b2; --line:#2a2f3a; --panel:#171a21; --code:#1e222b; --accent:#a78bfa; color-scheme:dark; }
        @media (prefers-color-scheme: light) { :root:not([data-theme="dark"]) { --bg:#fff; --text:#161a22; --muted:#5d6677; --line:#dfe3eb; --panel:#f5f6fa; --code:#eef0f5; --accent:#5b3cf0; color-scheme:light; } }
        :root[data-theme="light"] { --bg:#fff; --text:#161a22; --muted:#5d6677; --line:#dfe3eb; --panel:#f5f6fa; --code:#eef0f5; --accent:#5b3cf0; color-scheme:light; }
        html, body { margin:0; background:var(--bg); color:var(--text); }
        body { font:16px/1.7 Inter, system-ui, -apple-system, "Segoe UI", sans-serif; }
        .md { max-width:780px; margin:0 auto; padding:32px 24px 64px; }
        .md h1, .md h2, .md h3 { line-height:1.25; margin:1.6em 0 .6em; }
        .md h1 { font-size:2em; margin-top:0; } .md h2 { font-size:1.5em; padding-bottom:.3em; border-bottom:1px solid var(--line); }
        .md a { color:var(--accent); }
        .md img { max-width:100%; height:auto; border-radius:8px; }
        .md code { font:.88em ui-monospace, SFMono-Regular, Menlo, monospace; background:var(--code); padding:.15em .4em; border-radius:5px; }
        .md pre { background:#0d1117; border-radius:10px; padding:14px 16px; overflow:auto; }
        .md pre code { background:none; padding:0; color:#e6edf3; font-size:.85em; line-height:1.55; }
        .md blockquote { margin:1em 0; padding:.2em 1em; border-left:4px solid var(--accent); color:var(--muted); background:var(--panel); border-radius:0 8px 8px 0; }
        .md table { border-collapse:collapse; display:block; overflow:auto; }
        .md th, .md td { border:1px solid var(--line); padding:6px 12px; }
        .md hr { border:0; border-top:1px solid var(--line); margin:2em 0; }
        """;
}
