namespace CourseHub.Services;

public enum LessonKind { Video, Article }

public sealed record Lesson(
    string Id,
    string Title,
    string RelativePath,
    string FullPath,
    LessonKind Kind,
    long SizeBytes,
    double? DurationSeconds,
    string? SubtitlePath)
{
    public string MediaUrl => $"/media/{Id}";
    public string ThumbUrl => $"/thumb/{Id}";
    public string SubtitleUrl => $"/subs/{Id}.vtt";
    /// <summary>Slides, PDFs, zips… whose file names start with this lesson's name.</summary>
    public IReadOnlyList<Attachment> Attachments { get; init; } = [];
}

/// <summary>A downloadable file that belongs to a lesson or section.</summary>
public sealed record Attachment(string Id, string Name, string FullPath, long SizeBytes)
{
    public string Url => $"/attachment/{Id}";
    public string Extension => Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
    public bool OpensInBrowser => Extension is "pdf" or "txt" or "md" or "png" or "jpg" or "jpeg" or "gif" or "svg" or "webp";
}

/// <summary>A folder of courses. Library #1 comes from configuration; admins can add more.</summary>
public sealed record LibraryInfo(int Id, string Name, string Root)
{
    /// <summary>Prefix for lesson/course keys so libraries can't collide. Empty for #1, which keeps older data valid.</summary>
    public string KeyPrefix => Id == 1 ? "" : $"@{Id}/";
}

public sealed record Section(string Title, IReadOnlyList<Lesson> Lessons)
{
    public double TotalSeconds => Lessons.Sum(l => l.DurationSeconds ?? 0);
    /// <summary>Files in the section folder that don't belong to a specific lesson.</summary>
    public IReadOnlyList<Attachment> Files { get; init; } = [];
    /// <summary>Parent folders of a nested section ("Part One" for Part One/Basics); null at the top level.</summary>
    public string? Group { get; init; }
    public string FullTitle => Group is null ? Title : $"{Group} / {Title}";
}

/// <summary>A folder of code or other material that ships with a course.</summary>
public sealed record ResourceFolder(string Id, string Name, string RelativePath, string FullPath);

public sealed record Course(
    string Id,
    string Title,
    string FolderName,
    string FullPath,
    IReadOnlyList<Section> Sections,
    IReadOnlyList<ResourceFolder> Resources,
    string? Author = null,
    string? Description = null,
    string? CoverPath = null)
{
    public IEnumerable<Lesson> AllLessons => Sections.SelectMany(s => s.Lessons);
    public int LessonCount => Sections.Sum(s => s.Lessons.Count);
    public int VideoCount => AllLessons.Count(l => l.Kind == LessonKind.Video);
    public double TotalSeconds => Sections.Sum(s => s.TotalSeconds);
    public string CoverUrl => $"/cover/{Id}";
    public int LibraryId { get; init; } = 1;
    /// <summary>Total size of lessons and attachments on disk.</summary>
    public long TotalBytes { get; init; }
}

/// <summary>Optional course.json inside a course folder.</summary>
public sealed class CourseManifest
{
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    /// <summary>Image file name inside the course folder, e.g. "cover.jpg".</summary>
    public string? Cover { get; set; }
    /// <summary>Custom sections for a flat course. Start is the 1-based lesson number where the section begins.</summary>
    public List<ManifestSection>? Sections { get; set; }
}

public sealed class ManifestSection
{
    public string Title { get; set; } = "";
    public int Start { get; set; }
}
