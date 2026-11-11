using System.Diagnostics;

namespace Lyceum.Services;

public sealed record CodeFile(string RelativePath, long Size, int Depth);

/// <summary>Lists and reads files inside a course's resource folders, and opens them in an editor.</summary>
public sealed class CodeBrowser(ILogger<CodeBrowser> logger)
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", ".git", ".vs", ".idea", "target", "BenchmarkDotNet.Artifacts", "TestResults", "packages" };

    private static readonly HashSet<string> BinaryExt = new(StringComparer.OrdinalIgnoreCase)
        { ".dll", ".exe", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".zip", ".db", ".db-shm", ".db-wal", ".mp4", ".pdf", ".woff", ".woff2", ".ttf", ".nupkg", ".snk" };

    public const int MaxFiles = 3000;
    public const long MaxViewBytes = 1_000_000;

    public List<CodeFile> ListFiles(ResourceFolder folder)
    {
        var result = new List<CodeFile>();
        Walk(folder.FullPath, folder.FullPath, 0, result);
        return result;
    }

    private static void Walk(string root, string dir, int depth, List<CodeFile> into)
    {
        if (into.Count >= MaxFiles) return;
        IEnumerable<string> files, dirs;
        try
        {
            files = Directory.EnumerateFiles(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
            dirs = Directory.EnumerateDirectories(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (UnauthorizedAccessException) { return; }

        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            if (SkipDirs.Contains(name) || name.StartsWith('.')) continue;
            Walk(root, d, depth + 1, into);
        }
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            if (name == ".DS_Store") continue;
            into.Add(new CodeFile(Path.GetRelativePath(root, f), new FileInfo(f).Length, depth));
            if (into.Count >= MaxFiles) return;
        }
    }

    /// <summary>Resolves a path inside the folder, refusing anything that escapes it.</summary>
    public static string? Resolve(ResourceFolder folder, string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(folder.FullPath, relativePath));
        return full.StartsWith(folder.FullPath + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }

    public (string? Text, string? Reason) Read(ResourceFolder folder, string relativePath)
    {
        var full = Resolve(folder, relativePath);
        if (full is null) return (null, "File not found.");
        if (BinaryExt.Contains(Path.GetExtension(full))) return (null, "Binary file — open it in an editor or Finder.");
        var size = new FileInfo(full).Length;
        if (size > MaxViewBytes) return (null, $"File is {size / 1024} KB, too large to preview.");
        var text = File.ReadAllText(full);
        if (text.Contains('\0')) return (null, "Binary file — open it in an editor or Finder.");
        return (text, null);
    }

    public static string Language(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" or ".csx" => "csharp",
        ".csproj" or ".props" or ".targets" or ".xml" or ".config" or ".sln" or ".slnx" or ".xaml" => "xml",
        ".json" => "json",
        ".js" or ".mjs" => "javascript",
        ".ts" => "typescript",
        ".html" or ".htm" or ".razor" or ".cshtml" => "xml",
        ".css" => "css",
        ".md" => "markdown",
        ".rs" => "rust",
        ".toml" => "ini",
        ".yml" or ".yaml" => "yaml",
        ".sh" => "bash",
        ".ps1" => "powershell",
        ".sql" => "sql",
        ".http" => "http",
        ".py" => "python",
        ".fs" => "fsharp",
        ".dockerfile" => "dockerfile",
        _ => Path.GetFileName(path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ? "dockerfile" : "plaintext",
    };

    /// <summary>Opens a folder or file with a macOS app. Returns an error message, or null on success.</summary>
    public async Task<string?> OpenAsync(string target, string with)
    {
        string[] args = with switch
        {
            "vscode" => ["-a", "Visual Studio Code", target],
            "rider" => ["-a", "Rider", target],
            "finder" => File.Exists(target) ? ["-R", target] : [target],
            _ => throw new ArgumentOutOfRangeException(nameof(with)),
        };
        if (!OperatingSystem.IsMacOS()) return "Opening apps is only supported on macOS.";
        try
        {
            var (code, err) = await MediaTools.RunAsync("/usr/bin/open", args, TimeSpan.FromSeconds(15));
            if (code == 0) return null;
            logger.LogWarning("open {Args} failed: {Err}", string.Join(' ', args), err);
            return with switch
            {
                "vscode" => "Visual Studio Code isn't installed.",
                "rider" => "JetBrains Rider isn't installed.",
                _ => err.Trim(),
            };
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
