using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CourseHub.Data;
using Microsoft.EntityFrameworkCore;

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

/// <summary>What changed between two scans.</summary>
public sealed record LibraryDiff(IReadOnlyList<Course> AddedCourses, IReadOnlyList<Course> RemovedCourses, IReadOnlyList<(Course Course, Lesson Lesson)> AddedLessons)
{
    public bool IsEmpty => AddedCourses.Count == 0 && RemovedCourses.Count == 0 && AddedLessons.Count == 0;
}

/// <summary>
/// Scans the library folders. Every top-level folder is a course; sub-folders that contain
/// lessons become sections. Flat courses are split into sections at "Introduction"/"Overview" lessons.
/// Files named after a lesson (slides, PDFs, zips) become that lesson's attachments.
/// </summary>
public sealed partial class CourseLibrary(
    CourseHubOptions options,
    IWebHostEnvironment env,
    IDbContextFactory<ProgressDb> dbFactory,
    MediaProbe probe,
    ILogger<CourseLibrary> logger) : IDisposable
{
    public const string ManifestFile = "course.json";
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".webm", ".mkv", ".avi", ".wmv", ".flv", ".ts", ".mpg", ".mpeg" };
    private static readonly HashSet<string> ArticleExt = new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm", ".pdf" };
    private static readonly HashSet<string> SubtitleExt = new(StringComparer.OrdinalIgnoreCase) { ".vtt", ".srt" };
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", "wwwroot", "Properties", "target" };
    /// <summary>Cover image names, in priority order (Plex/Jellyfin/Kodi conventions included). Matched case-insensitively.</summary>
    private static readonly string[] CoverNames = ["cover", "folder", "poster", "thumb", "thumbnail", "artwork", "course", "banner"];
    private static readonly string[] ImageExt = [".jpg", ".jpeg", ".png", ".webp", ".avif", ".gif"];
    private static readonly HashSet<string> SkipFiles = new(
        new[] { ManifestFile, "Thumbs.db", "desktop.ini" }.Concat(CoverNames.SelectMany(n => ImageExt.Select(e => n + e))),
        StringComparer.OrdinalIgnoreCase);
    private static readonly string[] ProjectMarkers = ["*.csproj", "*.sln", "*.slnx", "package.json", "Cargo.toml", "*.fsproj"];
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ConcurrentDictionary<string, double?> _durationCache = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly List<FileSystemWatcher> _watchers = [];
    private CancellationTokenSource? _debounce;
    private Timer? _timer;
    private volatile Snapshot _snapshot = Snapshot.Empty;
    private bool _scannedOnce;

    /// <summary>Raised after every rescan.</summary>
    public event Action? Changed;
    /// <summary>Raised when courses or lessons appeared/disappeared (not on the first scan after startup).</summary>
    public event Action<LibraryDiff>? Diff;

    private sealed record Snapshot(
        IReadOnlyList<LibraryInfo> Libraries,
        IReadOnlyList<Course> Courses,
        IReadOnlyDictionary<string, Lesson> Lessons,
        IReadOnlyDictionary<string, (Course Course, Lesson Lesson)> ByPath,
        IReadOnlyDictionary<string, (Course Course, ResourceFolder Folder)> Resources,
        IReadOnlyDictionary<string, (Course Course, Attachment File)> Attachments,
        DateTime ScannedAt)
    {
        public static readonly Snapshot Empty = new([], [], new Dictionary<string, Lesson>(),
            new Dictionary<string, (Course, Lesson)>(), new Dictionary<string, (Course, ResourceFolder)>(),
            new Dictionary<string, (Course, Attachment)>(), DateTime.MinValue);
    }

    /// <summary>The folder of library #1 (from configuration).</summary>
    public string RootPath => Path.GetFullPath(Path.Combine(env.ContentRootPath, options.CoursesRoot));

    public IReadOnlyList<LibraryInfo> Libraries => _snapshot.Libraries;
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

    public (Course Course, Attachment File)? GetAttachment(string id) =>
        _snapshot.Attachments.TryGetValue(id, out var x) ? x : null;

    // ---------- access control ----------

    /// <summary>Courses a user may see. <paramref name="allowed"/> null means every library.</summary>
    public IEnumerable<Course> CoursesFor(AccessScope? scope) =>
        scope is null ? Courses : Courses.Where(scope.Allows);

    public static bool CanAccess(Course course, AccessScope? scope) => scope is null || scope.Allows(course);

    // ---------- libraries ----------

    public async Task<string?> AddLibraryAsync(string name, string path)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the library a name.";
        var full = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(full)) return $"Folder {full} doesn't exist (inside Docker, mount it into the container first).";
        if (_snapshot.Libraries.Any(l => Overlaps(l.Root, full))) return "That folder is inside (or contains) another library.";
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Libraries.Add(new LibraryEntity { Name = name.Trim(), Path = full, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        await RescanAsync();
        return null;
    }

    public async Task<string?> UpdateLibraryAsync(int id, string name, string? path)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var lib = await db.Libraries.FindAsync(id);
        if (lib is null) return "Library not found.";
        if (string.IsNullOrWhiteSpace(name)) return "Give the library a name.";
        lib.Name = name.Trim();
        if (id != 1 && path is not null)
        {
            var full = Path.GetFullPath(path.Trim());
            if (!Directory.Exists(full)) return $"Folder {full} doesn't exist.";
            if (_snapshot.Libraries.Any(l => l.Id != id && Overlaps(l.Root, full))) return "That folder is inside (or contains) another library.";
            lib.Path = full;
        }
        await db.SaveChangesAsync();
        await RescanAsync();
        return null;
    }

    /// <summary>Removes a library from CourseHub. Files on disk and users' progress are kept.</summary>
    public async Task<string?> RemoveLibraryAsync(int id)
    {
        if (id == 1) return "The main library comes from the server configuration and can't be removed.";
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Libraries.Where(l => l.Id == id).ExecuteDeleteAsync();
        await RescanAsync();
        return null;
    }

    private static bool Overlaps(string a, string b)
    {
        a = a.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        b = b.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal);
    }

    private async Task<List<LibraryInfo>> LoadLibrariesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.Libraries.AsNoTracking().OrderBy(l => l.Id).ToListAsync();
        if (rows.All(r => r.Id != 1))
        {
            var main = new LibraryEntity { Id = 1, Name = "Courses", Path = "(configuration)", CreatedAt = DateTime.UtcNow };
            db.Libraries.Add(main);
            await db.SaveChangesAsync();
            rows.Insert(0, main);
        }
        // Library #1 always follows the configured folder, so changing CourseHub__CoursesRoot just works.
        return rows.Select(r => new LibraryInfo(r.Id, r.Name, r.Id == 1 ? RootPath : r.Path)).ToList();
    }

    // ---------- watching ----------

    public void StartWatching()
    {
        if (options.RescanIntervalMinutes > 0)
        {
            var every = TimeSpan.FromMinutes(options.RescanIntervalMinutes);
            _timer = new Timer(_ => _ = RescanAsync(), null, every, every);
        }
        RestartWatchers();
    }

    private void RestartWatchers()
    {
        lock (_watchers)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
            if (!options.WatchForChanges) return;
            foreach (var lib in _snapshot.Libraries.Where(l => Directory.Exists(l.Root)))
            {
                try
                {
                    _watchers.Add(CreateWatcher(lib.Root));
                }
                catch (Exception ex)
                {
                    // e.g. inotify limits on Linux with very large libraries; the timer (if set) still works.
                    logger.LogWarning(ex, "Could not watch {Root} for changes; use Rescan or RescanIntervalMinutes instead", lib.Root);
                }
            }
        }
    }

    private FileSystemWatcher CreateWatcher(string root)
    {
        var ignore = new HashSet<string>(options.IgnoreFolders, StringComparer.OrdinalIgnoreCase);
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
        };
        void OnChange(object? _, FileSystemEventArgs e)
        {
            var rel = Path.GetRelativePath(root, e.FullPath);
            var first = rel.Split(Path.DirectorySeparatorChar)[0];
            if (first.StartsWith('.') || ignore.Contains(first) || Path.GetFileName(e.FullPath).StartsWith('.')) return;
            ScheduleRescan();
        }
        watcher.Created += OnChange;
        watcher.Deleted += OnChange;
        watcher.Changed += OnChange;
        watcher.Renamed += (s, e) => OnChange(s, e);
        watcher.EnableRaisingEvents = true;
        logger.LogInformation("Watching {Root} for course changes", root);
        return watcher;
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
        lock (_watchers) foreach (var w in _watchers) w.Dispose();
        _timer?.Dispose();
    }

    // ---------- course.json ----------

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

    // ---------- scanning ----------

    public async Task RescanAsync()
    {
        await _scanLock.WaitAsync();
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var libraries = await LoadLibrariesAsync();
            var librariesChanged = !libraries.SequenceEqual(_snapshot.Libraries);
            var previous = _snapshot;
            var snapshot = await Task.Run(() => Scan(libraries));
            _snapshot = snapshot;
            if (librariesChanged && (_watchers.Count > 0 || _timer is not null)) RestartWatchers();

            try { Changed?.Invoke(); } catch (Exception ex) { logger.LogWarning(ex, "Change handler failed"); }
            if (_scannedOnce)
            {
                var diff = Compare(previous, snapshot);
                if (!diff.IsEmpty)
                {
                    try { Diff?.Invoke(diff); } catch (Exception ex) { logger.LogWarning(ex, "Diff handler failed"); }
                }
            }
            _scannedOnce = true;
            logger.LogInformation("Scanned {Courses} courses / {Lessons} lessons in {Libraries} libraries in {Ms} ms",
                snapshot.Courses.Count, snapshot.Lessons.Count, libraries.Count, sw.ElapsedMilliseconds);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private static LibraryDiff Compare(Snapshot before, Snapshot after)
    {
        var oldCourses = before.Courses.Select(c => c.Id).ToHashSet();
        var newCourses = after.Courses.Select(c => c.Id).ToHashSet();
        var added = after.Courses.Where(c => !oldCourses.Contains(c.Id)).ToList();
        var removed = before.Courses.Where(c => !newCourses.Contains(c.Id)).ToList();
        var addedLessons = after.ByPath.Where(kv => !before.ByPath.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        return new LibraryDiff(added, removed, addedLessons);
    }

    private Snapshot Scan(IReadOnlyList<LibraryInfo> libraries)
    {
        var ignore = new HashSet<string>(options.IgnoreFolders, StringComparer.OrdinalIgnoreCase);
        var courses = new List<Course>();
        foreach (var lib in libraries)
        {
            if (!Directory.Exists(lib.Root))
            {
                logger.LogWarning("Library {Name} folder {Root} does not exist", lib.Name, lib.Root);
                continue;
            }
            foreach (var dir in Directory.EnumerateDirectories(lib.Root))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || ignore.Contains(name)) continue;
                try
                {
                    var course = BuildCourse(lib, dir);
                    if (course.LessonCount > 0) courses.Add(course);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Skipping {Folder}", dir);
                }
            }
        }

        courses.Sort((a, b) => NaturalComparer.Instance.Compare(a.Title, b.Title));
        var lessons = courses.SelectMany(c => c.AllLessons).ToDictionary(l => l.Id);
        var byPath = courses.SelectMany(c => c.AllLessons.Select(l => (c, l))).ToDictionary(x => x.l.RelativePath, x => (x.c, x.l));
        var resources = courses.SelectMany(c => c.Resources.Select(r => (c, r))).ToDictionary(x => x.r.Id, x => (x.c, x.r));
        var attachments = new Dictionary<string, (Course, Attachment)>();
        foreach (var c in courses)
        {
            foreach (var a in c.AllLessons.SelectMany(l => l.Attachments).Concat(c.Sections.SelectMany(s => s.Files)))
                attachments.TryAdd(a.Id, (c, a));
        }
        return new Snapshot(libraries, courses, lessons, byPath, resources, attachments, DateTime.UtcNow);
    }

    private Course BuildCourse(LibraryInfo lib, string courseDir)
    {
        var manifest = ReadManifest(courseDir);
        var sections = new List<Section>();
        var resources = new List<ResourceFolder>();

        // Lessons directly in the course folder.
        var (rootLessons, rootFiles) = LessonsIn(lib, courseDir);
        var subSections = new List<Section>();
        foreach (var sub in SubDirs(courseDir))
            CollectSections(lib, sub, [CleanTitle(Path.GetFileName(sub), isFile: false)], subSections, resources);

        if (rootLessons.Count > 0)
        {
            if (manifest?.Sections is { Count: > 0 } custom)
                sections.AddRange(ManifestSections(rootLessons, custom));
            else if (subSections.Count == 0 && rootLessons.Count >= 12)
                sections.AddRange(SplitIntoSections(rootLessons));
            else
                sections.Add(new Section(subSections.Count == 0 ? "Course Content" : "Main Lessons", rootLessons));
        }
        // Loose files in the course folder go with the first section.
        if (rootFiles.Count > 0 && sections.Count > 0) sections[0] = sections[0] with { Files = rootFiles };
        sections.AddRange(subSections);

        string? cover = null;
        if (!string.IsNullOrWhiteSpace(manifest?.Cover))
        {
            var c = Path.GetFullPath(Path.Combine(courseDir, manifest.Cover));
            if (c.StartsWith(courseDir + Path.DirectorySeparatorChar) && File.Exists(c)) cover = c;
        }
        cover ??= FindCover(courseDir);
        // Changes when the cover (or, without one, the first video) changes, so browsers fetch the new image.
        var coverSource = cover ?? sections.SelectMany(s => s.Lessons).FirstOrDefault(l => l.Kind == LessonKind.Video)?.FullPath;
        var coverVersion = coverSource is null ? 0 : File.GetLastWriteTimeUtc(coverSource).Ticks / TimeSpan.TicksPerSecond;

        var folder = Path.GetFileName(courseDir);
        var bytes = sections.Sum(s => s.Lessons.Sum(l => l.SizeBytes + l.Attachments.Sum(a => a.SizeBytes)) + s.Files.Sum(f => f.SizeBytes));
        return new Course(
            StableId(lib.KeyPrefix + Path.GetRelativePath(lib.Root, courseDir)),
            string.IsNullOrWhiteSpace(manifest?.Title) ? folder : manifest.Title.Trim(),
            lib.KeyPrefix + folder, courseDir, sections, resources,
            manifest?.Author, manifest?.Description, cover)
        {
            LibraryId = lib.Id,
            TotalBytes = bytes,
            CoverVersion = coverVersion,
        };
    }

    /// <summary>Finds cover.jpg, Folder.PNG, poster.webp… in a course folder, ignoring case (Linux file systems are case-sensitive).</summary>
    private static string? FindCover(string courseDir)
    {
        var files = Directory.EnumerateFiles(courseDir).Select(f => (Path: f, Name: Path.GetFileName(f))).ToList();
        foreach (var name in CoverNames)
            foreach (var ext in ImageExt)
                if (files.FirstOrDefault(f => f.Name.Equals(name + ext, StringComparison.OrdinalIgnoreCase)) is { Path: not null } hit)
                    return hit.Path;
        return null;
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
    /// <param name="path">Cleaned folder names from the course folder down to <paramref name="dir"/>, e.g. ["Part One", "Basics"].</param>
    private bool CollectSections(LibraryInfo lib, string dir, List<string> path, List<Section> into, List<ResourceFolder> resources)
    {
        var title = string.Join(" / ", path);
        if (IsCodeProject(dir))
        {
            AddResource(lib, dir, title, resources);
            return false;
        }
        var (lessons, files) = LessonsIn(lib, dir);
        var any = lessons.Count > 0;
        // Nested folders keep their parent path as a group, so the sidebar can show Part → Chapter.
        if (any) into.Add(new Section(path[^1], lessons) { Files = files, Group = path.Count > 1 ? string.Join(" / ", path[..^1]) : null });
        var childResources = new List<ResourceFolder>();
        foreach (var sub in SubDirs(dir))
            any |= CollectSections(lib, sub, [.. path, CleanTitle(Path.GetFileName(sub), isFile: false)], into, childResources);

        // A folder without any lessons is course material (code, slides...): show it as one resource.
        if (!any && Directory.EnumerateFileSystemEntries(dir).Any(e => !Path.GetFileName(e).StartsWith('.')))
            AddResource(lib, dir, title, resources);
        else
            resources.AddRange(childResources);
        return any;
    }

    private static void AddResource(LibraryInfo lib, string dir, string title, List<ResourceFolder> into)
    {
        var rel = lib.KeyPrefix + Path.GetRelativePath(lib.Root, dir);
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

    /// <summary>Lessons in a folder, plus files that don't belong to any lesson.</summary>
    private (List<Lesson> Lessons, List<Attachment> Files) LessonsIn(LibraryInfo lib, string dir)
    {
        var all = Directory.EnumerateFiles(dir)
            .Where(f => !Path.GetFileName(f).StartsWith('.') && !SkipFiles.Contains(Path.GetFileName(f)))
            .OrderBy(f => Path.GetFileName(f), NaturalComparer.Instance)
            .ToList();
        var videos = all.Where(f => VideoExt.Contains(Path.GetExtension(f))).ToList();
        var stems = videos.Select(v => Path.GetFileNameWithoutExtension(v)).OrderByDescending(s => s.Length).ToList();

        // Anything (other than subtitles) whose name starts with a video's name belongs to that video:
        // "3. Generics.mp4" gets "3. Generics.pdf", "3. Generics - slides.pptx", "3. Generics_code.zip".
        string? OwnerStem(string file)
        {
            var name = Path.GetFileName(file);
            return stems.FirstOrDefault(stem =>
                name.Length > stem.Length && name.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                && " .-_([".Contains(name[stem.Length]));
        }

        var attachmentsByStem = new Dictionary<string, List<Attachment>>(StringComparer.OrdinalIgnoreCase);
        var looseFiles = new List<Attachment>();
        var lessonFiles = new List<string>();
        var subtitles = new List<string>();
        foreach (var f in all)
        {
            var ext = Path.GetExtension(f);
            if (VideoExt.Contains(ext)) { lessonFiles.Add(f); continue; }
            if (SubtitleExt.Contains(ext)) { subtitles.Add(f); continue; }
            var owner = OwnerStem(f);
            if (owner is not null)
            {
                if (!attachmentsByStem.TryGetValue(owner, out var list)) attachmentsByStem[owner] = list = [];
                list.Add(MakeAttachment(lib, f));
            }
            else if (ArticleExt.Contains(ext)) lessonFiles.Add(f);
            else looseFiles.Add(MakeAttachment(lib, f));
        }

        var result = new Lesson[lessonFiles.Count];
        Parallel.For(0, lessonFiles.Count, i =>
        {
            var f = lessonFiles[i];
            var info = new FileInfo(f);
            var kind = VideoExt.Contains(info.Extension) ? LessonKind.Video : LessonKind.Article;
            var rel = lib.KeyPrefix + Path.GetRelativePath(lib.Root, f);
            double? duration = kind == LessonKind.Video
                ? _durationCache.GetOrAdd($"{f}|{info.Length}|{info.LastWriteTimeUtc.Ticks}", _ => Mp4Duration.TryRead(f) ?? probe.Duration(f))
                : null;
            string? subtitle = null;
            var stem = Path.GetFileNameWithoutExtension(f);
            if (kind == LessonKind.Video)
            {
                var stemPath = Path.Combine(dir, stem);
                subtitle = subtitles
                    .Where(s => s.StartsWith(stemPath + ".", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(s => Path.GetExtension(s).Equals(".vtt", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(s => s.Length)
                    .FirstOrDefault();
            }
            result[i] = new Lesson(StableId(rel), CleanTitle(info.Name, isFile: true), rel, f, kind, info.Length, duration, subtitle)
            {
                Attachments = kind == LessonKind.Video && attachmentsByStem.TryGetValue(stem, out var a) ? a : [],
            };
        });
        return ([.. result], looseFiles);
    }

    private static Attachment MakeAttachment(LibraryInfo lib, string file)
    {
        var rel = lib.KeyPrefix + Path.GetRelativePath(lib.Root, file);
        return new Attachment(StableId("att:" + rel), Path.GetFileName(file), file, new FileInfo(file).Length);
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
