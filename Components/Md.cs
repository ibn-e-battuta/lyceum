using System.Text.RegularExpressions;
using Markdig;
using Microsoft.AspNetCore.Components;

namespace Lyceum.Components;

/// <summary>Renders AI output as Markdown. Raw HTML is disabled so model output can't inject markup.</summary>
public static partial class Md
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseEmphasisExtras()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .Build();

    /// <param name="citation">Maps a citation number like [2] to a link, or null to leave it as text.</param>
    public static MarkupString Render(string markdown, Func<int, string?>? citation = null)
    {
        var html = Markdown.ToHtml(markdown ?? "", Pipeline);
        if (citation is not null)
        {
            html = Cite().Replace(html, m =>
            {
                var n = int.Parse(m.Groups[1].Value);
                return citation(n) is { } href ? $"<a class=\"cite\" href=\"{href}\">[{n}]</a>" : m.Value;
            });
        }
        return new MarkupString(html);
    }

    [GeneratedRegex(@"\[(\d{1,2})\]")] private static partial Regex Cite();
}
