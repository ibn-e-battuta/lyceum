namespace CourseHub.Components;

public static class Format
{
    /// <summary>Lesson length, e.g. "4:05" or "1:02:10".</summary>
    public static string Clock(double? seconds)
    {
        if (seconds is null or <= 0) return "";
        var t = TimeSpan.FromSeconds(Math.Round(seconds.Value));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Course or section length, e.g. "12h 5m" or "45min".</summary>
    public static string Long(double seconds)
    {
        if (seconds <= 0) return "";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))}min";
    }

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {units[i]}";
    }

    public static string Ago(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays}d ago";
        return utc.ToLocalTime().ToString("MMM d");
    }

    /// <summary>Deterministic gradient per course so cards are recognisable.</summary>
    public static string Gradient(string seed)
    {
        var h = 0;
        foreach (var ch in seed) h = (h * 31 + ch) & 0x7fffffff;
        var a = h % 360;
        var b = (a + 40 + h / 360 % 60) % 360;
        return $"linear-gradient(135deg, hsl({a} 65% 45%), hsl({b} 70% 30%))";
    }

    public static string Initials(string title) =>
        string.Concat(title.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetter(w[0]) && w.Length > 2 || char.IsUpper(w[0]))
            .Take(2).Select(w => char.ToUpperInvariant(w[0])));
}
