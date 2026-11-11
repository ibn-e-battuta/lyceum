using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CourseHub.Services;

public sealed class CourseHubOptions
{
    public string CoursesRoot { get; set; } = "..";
    public string[] IgnoreFolders { get; set; } = [];
    public bool WatchForChanges { get; set; } = true;
    /// <summary>Also rescan on a timer. File watching doesn't work on some network shares and Docker mounts.</summary>
    public int RescanIntervalMinutes { get; set; }
    /// <summary>Where the keys that encrypt sign-in cookies are kept.</summary>
    public string KeysFolder { get; set; } = "keys";
    /// <summary>Set when running behind a reverse proxy so client IPs and https are detected.</summary>
    public bool TrustForwardedHeaders { get; set; }
}

/// <summary>
/// Scans the courses folder. Every top-level folder is a course; sub-folders that contain
/// lessons become sections. Flat courses are split into sections at "Introduction"/"Overview" lessons.
/// </summary>
public sealed partial class CourseLibrary(CourseHubOptions options, IWebHostEnvironment env, ILogger<CourseLibrary> logger) : IDisposable
{
    private static readonly HashSet<string> SubtitleExt = new(StringComparer.OrdinalIgnoreCase) { ".vtt", ".srt" };
    public const string ManifestFile = "course.json";
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounce;

    /// <summary>Raised after every rescan.</summary>
    public event Action? Changed;
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".webm", ".mkv" };
    private static readonly HashSet<string> ArticleExt = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm", ".pdf" };
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", "wwwroot", "Properties", "target" };
    private static readonly string[] ProjectMarkers = ["*.csproj", "*.sln", "*.slnx", "package.json", "Cargo.toml", "*.fsproj"];

    private readonly ConcurrentDictionary<string, double?> _durationCache = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private volatile Snapshot _snapshot = Snapshot.Empty;

    private sealed record Snapshot(
        IReadOnlyList<Course> Courses,
        IReadOnlyDictionary<string, Lesson> Lessons,
        IReadOnlyDictionary<string, (Course Course, Lesson Lesson)> ByPath,
        IReadOnlyDictionary<string, (Course Course, ResourceFolder Folder)> Resources,
        DateTime ScannedAt)
    {
        public static readonly Snapshot Empty = new([], new Dictionary<string, Lesson>(),
            new Dictionary<string, (Course, Lesson)>(), new Dictionary<string, (Course, ResourceFolder)>(), DateTime.MinValue);
    }

    public string RootPath => Path.GetFullPath(Path.Combine(env.ContentRootPath, options.CoursesRoot));

    public IReadOnlyList<Course> Courses => _snapshot.Courses;
    public DateTime LastScan => _snapshot.ScannedAt;

    public Course? GetCourse(string id) => _snapshot.Courses.FirstOrDefault(c => c.Id == id);
    public Lesson? GetLesson(string id) => _snapshot.Lessons.GetValueOrDefault(id);
    public Course? GetCourseByFolder(string folder) => _snapshot.Courses.FirstOrDefault(c => c.FolderName == folder);
    public Course? CourseOf(Lesson lesson) => _snapshot.ByPath.TryGetValue(lesson.RelativePath, out var x) ? x.Course : null;

    public (Course Course, Lesson Lesson)? FindByPath(string relativePath) =>
        _snapshot.ByPath.TryGetValue(relativePath, out var x) ? x : null;

    public (Course Course, ResourceFolder Folder)? GetResource(string id) =>
        _snapshot.Resources.TryGetValue(id, out var x) ? x : null;

    private Timer? _timer;

    public void StartWatching()
    {
        if (options.RescanIntervalMinutes > 0)
        {
            var every = TimeSpan.FromMinutes(options.RescanIntervalMinutes);
            _timer = new Timer(_ => _ = RescanAsync(), null, every, every);
        }
        if (!options.WatchForChanges || !Directory.Exists(RootPath)) return;
        try
        {
            StartFileWatcher();
        }
        catch (Exception ex)
        {
            // e.g. inotify limits on Linux with very large libraries; the timer (if set) still works.
            logger.LogWarning(ex, "Could not watch {Root} for changes; use Rescan or RescanIntervalMinutes instead", RootPath);
        }
    }

    private void StartFileWatcher()
    {
        var ignore = new HashSet<string>(options.IgnoreFolders, StringComparer.OrdinalIgnoreCase);
        _watcher = new FileSystemWatcher(RootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
        };
        void OnChange(object? _, FileSystemEventArgs e)
        {
            var rel = Path.GetRelativePath(RootPath, e.FullPath);
            var first = rel.Split(Path.DirectorySeparatorChar)[0];
            if (first.StartsWith('.') || ignore.Contains(first) || Path.GetFileName(e.FullPath).StartsWith('.')) return;
            var ext = Path.GetExtension(e.FullPath);
            var relevant = ext.Length == 0 || VideoExt.Contains(ext) || ArticleExt.Contains(ext) || SubtitleExt.Contains(ext)
                           || Path.GetFileName(e.FullPath).Equals(ManifestFile, StringComparison.OrdinalIgnoreCase);
            if (relevant) ScheduleRescan();
        }
        _watcher.Created += OnChange;
        _watcher.Deleted += OnChange;
        _watcher.Changed += OnChange;
        _watcher.Renamed += (s, e) => OnChange(s, e);
        _watcher.EnableRaisingEvents = true;
        logger.LogInformation("Watching {Root} for course changes", RootPath);
    }

    private void ScheduleRescan()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _debounce, cts)?.Cancel();
        _ = Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ContinueWith(async t =>
        {
            if (!t.IsCanceled) await RescanAsync();
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _timer?.Dispose();
    }

    public static CourseManifest? ReadManifest(string courseDir)
    {
        var path = Path.Combine(courseDir, ManifestFile);
        if (!File.Exists(path)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<CourseManifest>(File.ReadAllText(path), JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveManifestAsync(Course course, CourseManifest manifest)
    {
        var path = Path.Combine(course.FullPath, ManifestFile);
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(manifest, JsonOpts));
        await RescanAsync();
    }

    public async Task RescanAsync()
    {
        await _scanLock.WaitAsync();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var snapshot = await Task.Run(Scan);
            _snapshot = snapshot;
            try { Changed?.Invoke(); } catch (Exception ex) { logger.LogWarning(ex, "Change handler failed"); }
            logger.LogInformation("Scanned {Courses} courses / {Lessons} lessons from {Root} in {Ms} ms",
                snapshot.Courses.Count, snapshot.Lessons.Count, RootPath, sw.ElapsedMilliseconds);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private Snapshot Scan()
    {
        var root = RootPath;
        if (!Directory.Exists(root))
        {
            logger.LogWarning("Courses root {Root} does not exist", root);
            return Snapshot.Empty with { ScannedAt = DateTime.UtcNow };
        }

        var ignore = new HashSet<string>(options.IgnoreFolders, StringComparer.OrdinalIgnoreCase);
        var courses = new List<Course>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || ignore.Contains(name)) continue;
            var course = BuildCourse(root, dir);
            if (course.LessonCount > 0) courses.Add(course);
        }

        courses.Sort((a, b) => NaturalComparer.Instance.Compare(a.Title, b.Title));
        var lessons = courses.SelectMany(c => c.AllLessons).ToDictionary(l => l.Id);
        var byPath = courses.SelectMany(c => c.AllLessons.Select(l => (c, l))).ToDictionary(x => x.l.RelativePath, x => (x.c, x.l));
        var resources = courses.SelectMany(c => c.Resources.Select(r => (c, r))).ToDictionary(x => x.r.Id, x => (x.c, x.r));
        return new Snapshot(courses, lessons, byPath, resources, DateTime.UtcNow);
    }

    private Course BuildCourse(string root, string courseDir)
    {
        var manifest = ReadManifest(courseDir);
        var sections = new List<Section>();
        var resources = new List<ResourceFolder>();

        // Lessons directly in the course folder.
        var rootLessons = LessonsIn(root, courseDir);
        var subSections = new List<Section>();
        foreach (var sub in SubDirs(courseDir))
            CollectSections(root, sub, Path.GetFileName(sub), subSections, resources);

        if (rootLessons.Count > 0)
        {
            if (manifest?.Sections is { Count: > 0 } custom)
                sections.AddRange(ManifestSections(rootLessons, custom));
            else if (subSections.Count == 0 && rootLessons.Count >= 12)
                sections.AddRange(SplitIntoSections(rootLessons));
            else
                sections.Add(new Section(subSections.Count == 0 ? "Course Content" : "Main Lessons", rootLessons));
        }
        sections.AddRange(subSections);

        string? cover = null;
        if (!string.IsNullOrWhiteSpace(manifest?.Cover))
        {
            var c = Path.GetFullPath(Path.Combine(courseDir, manifest.Cover));
            if (c.StartsWith(courseDir + Path.DirectorySeparatorChar) && File.Exists(c)) cover = c;
        }
        cover ??= new[] { "cover.jpg", "cover.jpeg", "cover.png", "cover.webp" }
            .Select(n => Path.Combine(courseDir, n)).FirstOrDefault(File.Exists);

        var folder = Path.GetFileName(courseDir);
        return new Course(
            StableId(Path.GetRelativePath(root, courseDir)),
            string.IsNullOrWhiteSpace(manifest?.Title) ? folder : manifest.Title.Trim(),
            folder, courseDir, sections, resources,
            manifest?.Author, manifest?.Description, cover);
    }

    private static IEnumerable<Section> ManifestSections(List<Lesson> lessons, List<ManifestSection> custom)
    {
        var ordered = custom.Where(c => c.Start >= 1).OrderBy(c => c.Start).ToList();
        if (ordered.Count == 0 || ordered[0].Start > 1) ordered.Insert(0, new ManifestSection { Title = "Getting Started", Start = 1 });
        for (var i = 0; i < ordered.Count; i++)
        {
            var from = ordered[i].Start - 1;
            var to = i + 1 < ordered.Count ? ordered[i + 1].Start - 1 : lessons.Count;
            if (from >= lessons.Count || to <= from) continue;
            yield return new Section(ordered[i].Title, lessons[from..Math.Min(to, lessons.Count)]);
        }
    }

    /// <returns>true when the folder (or a child) contains lessons.</returns>
    private bool CollectSections(string root, string dir, string title, List<Section> into, List<ResourceFolder> resources)
    {
        if (IsCodeProject(dir))
        {
            AddResource(root, dir, title, resources);
            return false;
        }
        var lessons = LessonsIn(root, dir);
        var any = lessons.Count > 0;
        if (any) into.Add(new Section(CleanTitle(title, isFile: false), lessons));
        var childResources = new List<ResourceFolder>();
        foreach (var sub in SubDirs(dir))
            any |= CollectSections(root, sub, $"{title} / {Path.GetFileName(sub)}", into, childResources);

        // A folder without any lessons is course material (code, slides...): show it as one resource.
        if (!any && Directory.EnumerateFileSystemEntries(dir).Any(e => !Path.GetFileName(e).StartsWith('.')))
            AddResource(root, dir, title, resources);
        else
            resources.AddRange(childResources);
        return any;
    }

    private static void AddResource(string root, string dir, string title, List<ResourceFolder> into)
    {
        var rel = Path.GetRelativePath(root, dir);
        into.Add(new ResourceFolder(StableId("res:" + rel), title, rel, dir));
    }

    private static IEnumerable<string> SubDirs(string dir) =>
        Directory.EnumerateDirectories(dir)
            .Where(d =>
            {
                var n = Path.GetFileName(d);
                return !n.StartsWith('.') && !SkipDirs.Contains(n);
            })
            .OrderBy(d => Path.GetFileName(d), NaturalComparer.Instance);

    public static bool IsCodeProject(string dir) =>
        ProjectMarkers.Any(m => Directory.EnumerateFiles(dir, m).Any());

    private List<Lesson> LessonsIn(string root, string dir)
    {
        var all = Directory.EnumerateFiles(dir).Where(f => !Path.GetFileName(f).StartsWith('.')).ToList();
        var subtitles = all.Where(f => SubtitleExt.Contains(Path.GetExtension(f))).ToList();
        var files = all
            .Where(f => VideoExt.Contains(Path.GetExtension(f)) || ArticleExt.Contains(Path.GetExtension(f)))
            .OrderBy(f => Path.GetFileName(f), NaturalComparer.Instance)
            .ToList();

        var result = new Lesson[files.Count];
        Parallel.For(0, files.Count, i =>
        {
            var f = files[i];
            var info = new FileInfo(f);
            var kind = VideoExt.Contains(info.Extension) ? LessonKind.Video : LessonKind.Article;
            var rel = Path.GetRelativePath(root, f);
            double? duration = kind == LessonKind.Video
                ? _durationCache.GetOrAdd($"{f}|{info.Length}|{info.LastWriteTimeUtc.Ticks}", _ => Mp4Duration.TryRead(f))
                : null;
            string? subtitle = null;
            if (kind == LessonKind.Video)
            {
                var stem = Path.Combine(dir, Path.GetFileNameWithoutExtension(f));
                subtitle = subtitles
                    .Where(s => s.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => Path.GetExtension(s).Equals(".vtt", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(s => s.Length)
                    .FirstOrDefault();
            }
            result[i] = new Lesson(StableId(rel), CleanTitle(info.Name, isFile: true), rel, f, kind, info.Length, duration, subtitle);
        });
        return [.. result];
    }

    /// <summary>Flat course: start a new section at each "Introduction"/"Overview" style lesson.</summary>
    private static IEnumerable<Section> SplitIntoSections(List<Lesson> lessons)
    {
        var chunks = new List<List<Lesson>> { new() };
        foreach (var l in lessons)
        {
            // An intro starts a new section, unless the current one only holds intros so far
            // (e.g. "Section Overview" followed by "Overview").
            var current = chunks[^1];
            if (SectionStart().IsMatch(l.Title) && current.Any(x => !SectionStart().IsMatch(x.Title)))
                chunks.Add([]);
            chunks[^1].Add(l);
            // A recap/wrap-up closes the section.
            if (SectionEnd().IsMatch(l.Title) && l != lessons[^1])
                chunks.Add([]);
        }
        chunks.RemoveAll(c => c.Count == 0);
        // Fold stray single-lesson chunks into the previous section.
        for (var i = chunks.Count - 1; i > 0; i--)
        {
            if (chunks[i].Count > 1) continue;
            chunks[i - 1].AddRange(chunks[i]);
            chunks.RemoveAt(i);
        }

        if (chunks.Count < 2) return [new Section("Course Content", lessons)];

        return chunks.Select((c, i) =>
        {
            // Name the section after its first "real" lesson, e.g. "Section 3: Creating Your First Skill".
            var named = c.FirstOrDefault(l => !SectionStart().IsMatch(l.Title) && !SectionEnd().IsMatch(l.Title));
            var title = i == 0 ? "Getting Started" : named is null ? $"Section {i + 1}" : $"Section {i + 1}: {named.Title}";
            return new Section(title, c);
        });
    }

    public static string CleanTitle(string name, bool isFile)
    {
        var t = isFile ? Path.GetFileNameWithoutExtension(name) : name;
        t = SiteSuffix().Replace(t, "");
        t = LeadingNumber().Replace(t, "");
        t = TrailingCopy().Replace(t, "");
        t = t.Replace("  ", " ").Trim();
        return t.Length == 0 ? name : t;
    }

    public static string StableId(string relativePath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/')));
        return Convert.ToHexStringLower(bytes)[..12];
    }

    [GeneratedRegex(@"^\s*\d+\s*[\.\-_)]?\s+")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"\s*[｜|_]\s*Hello Interview.*$", RegexOptions.IgnoreCase)]
    private static partial Regex SiteSuffix();

    [GeneratedRegex(@"(_\d+|\s*\(\d+\))$")]
    private static partial Regex TrailingCopy();

    [GeneratedRegex(@"^((Section|Module|Course)\s+)?(Introduction|Intro|Overview)$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionStart();

    [GeneratedRegex(@"(Recap|Wrap[\s-]?Up|Summary|Conclusion)$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionEnd();
}

/// <summary>Sorts "2. Foo" before "10. Bar".</summary>
public sealed partial class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return string.Compare(x, y, StringComparison.Ordinal);
        var px = Parts().Matches(x);
        var py = Parts().Matches(y);
        for (var i = 0; i < Math.Min(px.Count, py.Count); i++)
        {
            var a = px[i].Value;
            var b = py[i].Value;
            int c;
            if (char.IsDigit(a[0]) && char.IsDigit(b[0]))
            {
                var ta = a.TrimStart('0');
                var tb = b.TrimStart('0');
                c = ta.Length != tb.Length ? ta.Length.CompareTo(tb.Length) : string.CompareOrdinal(ta, tb);
            }
            else
            {
                c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            }
            if (c != 0) return c;
        }
        return px.Count.CompareTo(py.Count);
    }

    [GeneratedRegex(@"\d+|\D+")]
    private static partial Regex Parts();
}
