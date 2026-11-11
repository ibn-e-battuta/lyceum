using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace CourseHub.Services;

/// <summary>
/// UI translations. Keys are the English text, so a missing translation simply shows English.
/// Add a language by dropping Localization/xx.json next to the others and listing it in <see cref="Languages"/>.
/// </summary>
public sealed class Localizer
{
    public static readonly IReadOnlyDictionary<string, string> Languages = new Dictionary<string, string>
    {
        ["en"] = "English",
        ["de"] = "Deutsch",
        ["es"] = "Español",
        ["fr"] = "Français",
    };

    private readonly FrozenDictionary<string, FrozenDictionary<string, string>> _tables;

    public Localizer(IWebHostEnvironment env, ILogger<Localizer> logger)
    {
        var tables = new Dictionary<string, FrozenDictionary<string, string>>();
        var dir = Path.Combine(env.ContentRootPath, "Localization");
        if (Directory.Exists(dir))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? [];
                    tables[Path.GetFileNameWithoutExtension(file)] = map.Where(kv => kv.Value.Length > 0).ToFrozenDictionary();
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex, "Ignoring broken translation file {File}", file);
                }
            }
        }
        _tables = tables.ToFrozenDictionary();
    }

    public static string CurrentLanguage => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    public string this[string text] =>
        _tables.TryGetValue(CurrentLanguage, out var t) && t.TryGetValue(text, out var translated) ? translated : text;

    /// <summary>Translated format string, e.g. L["{0} lessons", n].</summary>
    public string this[string text, params object?[] args] => string.Format(CultureInfo.CurrentCulture, this[text], args);
}
