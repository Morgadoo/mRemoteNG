using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Avalonia.Media;
using mRemoteNG.Core.App.Info;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// A colour theme: a base palette (Themes/DarkTheme.axaml or LightTheme.axaml; the control styles in
/// Themes/Controls.axaml only reference its colours) plus colours for the palette keys (<see cref="PaletteKeys"/>). Built-in themes ship with the app; user themes
/// are JSON files in the settings folder's Themes directory.
/// </summary>
public sealed class ThemeDefinition
{
    /// <summary>
    /// The colour keys of the palette that a theme can change, with what they colour (docs/design-system.md §2).
    /// Each key is a <c>Color</c> resource with a <c>…Brush</c> of the same name; the theme editor lists them in
    /// this order. Themes written before a key existed simply lack it and get the base palette's (or a derived)
    /// value, see <see cref="ThemeCatalog.ResolveColors"/>.
    /// </summary>
    public static IReadOnlyList<(string Key, string Description)> PaletteKeys { get; } =
    [
        ("AppBg0", "Window and session area background"),
        ("AppBg1", "Sidebar, panels, header bar, menus, cards"),
        ("AppBg2", "Raised surfaces: inputs, buttons, tab strip"),
        ("AppBg3", "Selected (neutral) row, active tab, pressed"),
        ("AppBg4", "Hover"),
        ("TextPrimary", "Text"),
        ("TextSecondary", "Labels, secondary text, inactive tabs"),
        ("TextMuted", "Hints, placeholders, disabled text"),
        ("Accent", "Accent: primary buttons, focus, selection marker"),
        ("Border0", "Borders and dividers"),
        ("TextLink", "Links"),
        ("Warning", "Warnings, reconnecting"),
        ("AccentHover", "Accent under the pointer"),
        ("AccentActive", "Accent when pressed"),
        ("AccentSubtle", "Selected row background (translucent accent)"),
        ("OnAccent", "Text and icons on the accent colour"),
        ("Border1", "Strong borders: inputs, popups"),
        ("Success", "Connected, OK"),
        ("Danger", "Errors, destructive actions"),
        ("Overlay", "Modal scrim (translucent)"),
        ("ProtoSsh", "SSH icons and chips"),
        ("ProtoTelnet", "Telnet, Rlogin and Raw icons and chips"),
        ("ProtoRdp", "RDP icons and chips"),
        ("ProtoVnc", "VNC and ARD icons and chips"),
        ("ProtoHttp", "HTTP/HTTPS icons and chips"),
        ("ProtoPowerShell", "PowerShell icons and chips"),
        ("ProtoTerminal", "Terminal and WSL icons and chips"),
        ("ProtoSerial", "Serial icons and chips"),
        ("ProtoIntApp", "External application icons and chips"),
        ("ProtoAnyDesk", "AnyDesk icons and chips"),
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
/// palette keys of the design system), and user themes from <see cref="ApplicationPaths.UserThemesDirectory"/>.
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

    /// <summary>Colours of Themes/DarkTheme.axaml (the default dark palette, docs/design-system.md §2).</summary>
    public static ThemeDefinition Dark { get; } = BuiltIn(DarkName, true, new()
    {
        ["AppBg0"] = "#14161B", ["AppBg1"] = "#1A1D23", ["AppBg2"] = "#21252D", ["AppBg3"] = "#2A2F39", ["AppBg4"] = "#252A33",
        ["TextPrimary"] = "#E6E8EE", ["TextSecondary"] = "#A2A9B6", ["TextMuted"] = "#6B7385", ["TextLink"] = "#7AA7FF",
        ["Accent"] = "#4C8DFF", ["AccentHover"] = "#6AA0FF", ["AccentActive"] = "#3A78E6", ["AccentSubtle"] = "#2E4C8DFF",
        ["OnAccent"] = "#FFFFFF", ["Border0"] = "#2B303A", ["Border1"] = "#363C48",
        ["Success"] = "#3FB950", ["Warning"] = "#D29922", ["Danger"] = "#F85149", ["Overlay"] = "#73000000",
        ["ProtoSsh"] = "#3FB950", ["ProtoTelnet"] = "#8B949E", ["ProtoRdp"] = "#4C8DFF", ["ProtoVnc"] = "#A371F7",
        ["ProtoHttp"] = "#F0883E", ["ProtoPowerShell"] = "#56B6F7", ["ProtoTerminal"] = "#C9D1D9", ["ProtoSerial"] = "#D29922",
        ["ProtoIntApp"] = "#DB61A2", ["ProtoAnyDesk"] = "#EF443B",
    });

    /// <summary>Colours of Themes/LightTheme.axaml (the default light palette, docs/design-system.md §2).</summary>
    public static ThemeDefinition Light { get; } = BuiltIn(LightName, false, new()
    {
        ["AppBg0"] = "#F5F6F8", ["AppBg1"] = "#FFFFFF", ["AppBg2"] = "#F0F2F5", ["AppBg3"] = "#E4E8EE", ["AppBg4"] = "#EBEEF2",
        ["TextPrimary"] = "#1D2129", ["TextSecondary"] = "#4D5566", ["TextMuted"] = "#8A92A3", ["TextLink"] = "#2F6FED",
        ["Accent"] = "#2F6FED", ["AccentHover"] = "#4A82F0", ["AccentActive"] = "#2459C8", ["AccentSubtle"] = "#1F2F6FED",
        ["OnAccent"] = "#FFFFFF", ["Border0"] = "#E1E5EB", ["Border1"] = "#CDD3DC",
        ["Success"] = "#1F8F3A", ["Warning"] = "#B7791F", ["Danger"] = "#D1242F", ["Overlay"] = "#400F172A",
        ["ProtoSsh"] = "#3FB950", ["ProtoTelnet"] = "#8B949E", ["ProtoRdp"] = "#4C8DFF", ["ProtoVnc"] = "#A371F7",
        ["ProtoHttp"] = "#F0883E", ["ProtoPowerShell"] = "#56B6F7", ["ProtoTerminal"] = "#57606A", ["ProtoSerial"] = "#D29922",
        ["ProtoIntApp"] = "#DB61A2", ["ProtoAnyDesk"] = "#EF443B",
    });

    /// <summary>
    /// Legacy vs2015blue.vstheme on the light palette: white tool windows, blue-grey chrome #D6DBE9,
    /// yellow selection #FDF4BF / #FFF29D, text #1B293E, borders #8E9BBC, accent #007ACC, links #0066CC.
    /// </summary>
    public static ThemeDefinition Vs2015Blue { get; } = BuiltIn(Vs2015BlueName, false, new()
    {
        ["AppBg0"] = "#FFFFFF", ["AppBg1"] = "#D6DBE9", ["AppBg2"] = "#FFFFFF", ["AppBg3"] = "#FDF4BF", ["AppBg4"] = "#FFF8D9",
        ["TextPrimary"] = "#1B293E", ["TextSecondary"] = "#3C4B66", ["TextMuted"] = "#6D7A99", ["TextLink"] = "#0066CC",
        ["Accent"] = "#007ACC", ["AccentHover"] = "#1C97EA", ["AccentActive"] = "#0062A3", ["AccentSubtle"] = "#FFF29D",
        ["OnAccent"] = "#FFFFFF", ["Border0"] = "#C3CCDF", ["Border1"] = "#8E9BBC",
        ["Success"] = "#2E7D32", ["Warning"] = "#8A5A00", ["Danger"] = "#C42B1C", ["Overlay"] = "#40293955",
    });

    /// <summary>
    /// Legacy darcula.vstheme on the dark palette: window #3C3F41, tabs #353739, fields #45494A, selection #4B6EAF,
    /// text #BBBBBB / gray #999999, borders #2D2D2D / #646464, links #589DF6.
    /// </summary>
    public static ThemeDefinition Darcula { get; } = BuiltIn(DarculaName, true, new()
    {
        ["AppBg0"] = "#3C3F41", ["AppBg1"] = "#353739", ["AppBg2"] = "#45494A", ["AppBg3"] = "#4E5254", ["AppBg4"] = "#464A4D",
        ["TextPrimary"] = "#BBBBBB", ["TextSecondary"] = "#999999", ["TextMuted"] = "#787878", ["TextLink"] = "#589DF6",
        ["Accent"] = "#4B6EAF", ["AccentHover"] = "#5A7FC2", ["AccentActive"] = "#3F5E99", ["AccentSubtle"] = "#804B6EAF",
        ["OnAccent"] = "#FFFFFF", ["Border0"] = "#2D2D2D", ["Border1"] = "#646464",
        ["Success"] = "#499C54", ["Warning"] = "#D7BA7D", ["Danger"] = "#C75450", ["Overlay"] = "#73000000",
    });

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

    /// <summary>
    /// Every palette colour <paramref name="theme"/> shows: the base palette (<see cref="Dark"/> or <see cref="Light"/>)
    /// overlaid with the theme's own colours. Keys a theme does not define (themes saved by older versions only know
    /// the first twelve keys) fall back to the base palette, except the accent variants, which are derived from the
    /// theme's accent so that a custom accent is not paired with the default blue hover and selection.
    /// </summary>
    public static Dictionary<string, string> ResolveColors(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var basis = theme.IsDark ? Dark : Light;
        var colors = new Dictionary<string, string>(basis.Colors, StringComparer.Ordinal);
        foreach (var (key, value) in theme.Colors)
        {
            if (ThemeDefinition.IsValidColor(value))
                colors[key] = value.Trim();
        }

        if (ReferenceEquals(theme, basis) || theme.GetColor("Accent") is not { } accentText || !ThemeDefinition.IsValidColor(accentText))
            return colors;

        var accent = Color.Parse(accentText.Trim());
        if (theme.GetColor("AccentHover") is null)
            colors["AccentHover"] = ToHex(Mix(accent, Colors.White, theme.IsDark ? 0.15 : 0.12));
        if (theme.GetColor("AccentActive") is null)
            colors["AccentActive"] = ToHex(Mix(accent, Colors.Black, 0.12));
        if (theme.GetColor("AccentSubtle") is null)
            colors["AccentSubtle"] = ToHex(Color.FromArgb(theme.IsDark ? (byte)0x2E : (byte)0x1F, accent.R, accent.G, accent.B));
        return colors;
    }

    /// <summary>Linear mix of two colours (the alpha of <paramref name="from"/> is kept).</summary>
    internal static Color Mix(Color from, Color to, double amount) => Color.FromArgb(
        from.A,
        (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
        (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
        (byte)Math.Round(from.B + ((to.B - from.B) * amount)));

    internal static string ToHex(Color color) => color.A == 0xFF
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    private static ThemeDefinition BuiltIn(string name, bool isDark, Dictionary<string, string> colors) =>
        new() { Name = name, IsDark = isDark, Colors = new Dictionary<string, string>(colors, StringComparer.Ordinal), IsBuiltIn = true };
}
