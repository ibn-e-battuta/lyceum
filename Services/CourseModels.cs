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
}

public sealed record Section(string Title, IReadOnlyList<Lesson> Lessons)
{
    public double TotalSeconds => Lessons.Sum(l => l.DurationSeconds ?? 0);
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
