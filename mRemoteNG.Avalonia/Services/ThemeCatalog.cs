using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using mRemoteNG.Core.App.Info;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// A colour theme: a base palette (Themes/DarkTheme.axaml or LightTheme.axaml, which supply all control styles)
/// plus colours for the palette keys (<see cref="PaletteKeys"/>). Built-in themes ship with the app; user themes
/// are JSON files in the settings folder's Themes directory.
/// </summary>
public sealed class ThemeDefinition
{
    /// <summary>The colour keys of the palette that a theme can change, with what they colour.</summary>
    public static IReadOnlyList<(string Key, string Description)> PaletteKeys { get; } =
    [
        ("AppBg0", "Window and tree background"),
        ("AppBg1", "Panel headers, menus, status bar"),
        ("AppBg2", "Toolbars and buttons"),
        ("AppBg3", "Selection and hover"),
        ("AppBg4", "Strong hover"),
        ("TextPrimary", "Text"),
        ("TextSecondary", "Secondary text, inactive tabs"),
        ("TextMuted", "Muted text, hints"),
        ("Accent", "Accent (default buttons, focus)"),
        ("Border0", "Borders"),
        ("TextLink", "Links"),
        ("Warning", "Warnings"),
    ];

    public string Name { get; set; } = string.Empty;

    /// <summary>True: built on the dark palette (and the Fluent dark variant); false: light.</summary>
    public bool IsDark { get; set; } = true;

    /// <summary>Palette key → colour ("#rrggbb" or "#aarrggbb").</summary>
    public Dictionary<string, string> Colors { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public bool IsBuiltIn { get; init; }

    /// <summary>The file a user theme was loaded from or saved to.</summary>
    [JsonIgnore]
    public string? FilePath { get; set; }

    public ThemeDefinition Copy(string name) => new()
    {
        Name = name,
        IsDark = IsDark,
        Colors = new Dictionary<string, string>(Colors, StringComparer.Ordinal),
    };

    public string? GetColor(string key) => Colors.GetValueOrDefault(key);

    /// <summary>True for "#rgb", "#rrggbb" and "#aarrggbb".</summary>
    public static bool IsValidColor(string? value) =>
        value is not null && Regex.IsMatch(value.Trim(), "^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");
}

/// <summary>
/// The available themes: the built-in Dark and Light palettes, the legacy themes ported from the WinForms
/// app's <c>Themes/*.vstheme</c> files (VS2015 Blue and Darcula; their environment colours mapped onto the
/// palette keys), and user themes from <see cref="ApplicationPaths.UserThemesDirectory"/>.
/// </summary>
public sealed class ThemeCatalog
{
    public const string DarkName = "Dark";
    public const string LightName = "Light";
    public const string Vs2015BlueName = "VS2015 Blue";
    public const string DarculaName = "Darcula";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string? _userThemesDirectory;

    /// <param name="userThemesDirectory">Folder of user themes; null = the settings (or portable) folder's Themes.</param>
    public ThemeCatalog(string? userThemesDirectory = null)
    {
        _userThemesDirectory = userThemesDirectory;
    }

    public string UserThemesDirectory => _userThemesDirectory ?? ApplicationPaths.UserThemesDirectory;

    /// <summary>Colours of Themes/DarkTheme.axaml (VS2015 Dark).</summary>
    public static ThemeDefinition Dark { get; } = BuiltIn(DarkName, true,
        "#1e1e1e", "#252526", "#2d2d2d", "#3e3e42", "#555558", "#d4d4d4", "#a0a0a0", "#606060", "#007acc", "#3f3f46", "#569cd6", "#d7ba7d");

    /// <summary>Colours of Themes/LightTheme.axaml (VS2015 Light).</summary>
    public static ThemeDefinition Light { get; } = BuiltIn(LightName, false,
        "#f5f5f5", "#eaeaea", "#e1e1e1", "#cce8ff", "#d9d9d9", "#1e1e1e", "#444444", "#909090", "#007acc", "#cccccc", "#0066b8", "#8a5a00");

    /// <summary>
    /// Legacy vs2015blue.vstheme: tool windows #FFFFFF, window caption/shelf #D6DBE9, command bar #CFD6E5,
    /// selection #FDF4BF / #FFF29D, text #1B293E, borders #8E9BBC, status bar #007ACC, links #0066CC.
    /// </summary>
    public static ThemeDefinition Vs2015Blue { get; } = BuiltIn(Vs2015BlueName, false,
        "#ffffff", "#d6dbe9", "#cfd6e5", "#fdf4bf", "#fff29d", "#1b293e", "#3c4b66", "#8e9bbc", "#007acc", "#8e9bbc", "#0066cc", "#8a5a00");

    /// <summary>
    /// Legacy darcula.vstheme: window #3C3F41, tabs #353739, command bar #464A4D, selection #4B6EAF,
    /// selected tab #5A6D9E, text #BBBBBB / gray #999999, borders #2D2D2D, links #589DF6.
    /// </summary>
    public static ThemeDefinition Darcula { get; } = BuiltIn(DarculaName, true,
        "#3c3f41", "#353739", "#464a4d", "#4b6eaf", "#5a6d9e", "#bbbbbb", "#999999", "#787878", "#4b6eaf", "#2d2d2d", "#589df6", "#d7ba7d");

    public static IReadOnlyList<ThemeDefinition> BuiltInThemes { get; } = [Dark, Light, Vs2015Blue, Darcula];

    /// <summary>Built-in themes followed by the user's themes (sorted by name).</summary>
    public IReadOnlyList<ThemeDefinition> GetAll() => [.. BuiltInThemes, .. LoadUserThemes()];

    /// <summary>A theme by name (built-in first, case-insensitive), or null.</summary>
    public ThemeDefinition? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return BuiltInThemes.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? LoadUserThemes().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ThemeDefinition> LoadUserThemes()
    {
        if (!Directory.Exists(UserThemesDirectory))
            return [];

        var themes = new List<ThemeDefinition>();
        foreach (var file in Directory.EnumerateFiles(UserThemesDirectory, "*.json"))
        {
            try
            {
                var theme = JsonSerializer.Deserialize<ThemeDefinition>(File.ReadAllText(file), JsonOptions);
                if (theme is null || string.IsNullOrWhiteSpace(theme.Name))
                    continue;
                theme.Colors = theme.Colors
                    .Where(c => ThemeDefinition.IsValidColor(c.Value))
                    .ToDictionary(c => c.Key, c => c.Value.Trim(), StringComparer.Ordinal);
                theme.FilePath = file;
                themes.Add(theme);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken theme file is skipped, not fatal.
            }
        }

        return themes.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Validation errors for saving <paramref name="theme"/> as a user theme; empty when it can be saved.</summary>
    public IReadOnlyList<string> Validate(ThemeDefinition theme)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(theme.Name))
            errors.Add("Enter a name for the theme.");
        else if (BuiltInThemes.Any(t => string.Equals(t.Name, theme.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            errors.Add($"\"{theme.Name}\" is a built-in theme; choose another name.");

        foreach (var (key, value) in theme.Colors)
        {
            if (!ThemeDefinition.IsValidColor(value))
                errors.Add($"{key}: \"{value}\" is not a colour (use #rrggbb).");
        }

        return errors;
    }

    /// <summary>Saves a user theme (replacing one with the same name) and returns its file.</summary>
    public string Save(ThemeDefinition theme)
    {
        var errors = Validate(theme);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors), nameof(theme));

        theme.Name = theme.Name.Trim();
        Directory.CreateDirectory(UserThemesDirectory);
        var existing = LoadUserThemes().FirstOrDefault(t => string.Equals(t.Name, theme.Name, StringComparison.OrdinalIgnoreCase));
        var path = existing?.FilePath ?? Path.Combine(UserThemesDirectory, FileNameFor(theme.Name));
        File.WriteAllText(path, JsonSerializer.Serialize(theme, JsonOptions));
        theme.FilePath = path;
        return path;
    }

    public bool Delete(string name)
    {
        var theme = LoadUserThemes().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (theme?.FilePath is null)
            return false;
        File.Delete(theme.FilePath);
        return true;
    }

    private static string FileNameFor(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
        return safe.ToLower(CultureInfo.InvariantCulture) + ".json";
    }

    private static ThemeDefinition BuiltIn(string name, bool isDark, params string[] colors)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < ThemeDefinition.PaletteKeys.Count; i++)
            map[ThemeDefinition.PaletteKeys[i].Key] = colors[i];
        return new ThemeDefinition { Name = name, IsDark = isDark, Colors = map, IsBuiltIn = true };
    }
}
