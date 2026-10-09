using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using mRemoteNG.Core.Settings;
using ReactiveUI;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Applies the appearance settings to the running application:
/// <list type="bullet">
///   <item>Theme: swaps the mRemoteNG palette (Themes/DarkTheme.axaml ↔ Themes/LightTheme.axaml) and the
///         Fluent theme variant. "System" follows the OS preference and updates when it changes.</item>
///   <item>Named themes (<see cref="ThemeCatalog"/>: VS2015 Blue, Darcula, user themes): a fresh copy of the
///         dark or light palette is loaded and its colours replaced, so there is still exactly one palette.
///         Brushes are changed in place, so the theme editor's edits show immediately.</item>
///   <item>Font family/size for text and controls (local values in views still win).</item>
///   <item>Visibility of the main window's toolbar and status bar.</item>
/// </list>
/// Settings are persisted by <see cref="AppSettingsService"/>, not here.
/// </summary>
public sealed class ThemeService : ReactiveObject
{
    private const string ThemeFolder = "avares://mRemoteNG.Avalonia/Themes/";
    private static readonly Uri BaseUri = new("avares://mRemoteNG.Avalonia/");

    private ThemeMode _currentTheme = ThemeMode.Dark;
    private ThemeVariant? _effectiveVariant;
    private Styles? _appearanceStyles;
    private bool _followingSystem;
    private bool _paletteCustomized;
    private string? _currentThemeName;
    private string? _appliedThemeSignature;

    public static ThemeService Instance { get; } = new();

    private ThemeService()
    {
    }

    /// <summary>The selected mode (Dark, Light or System).</summary>
    public ThemeMode CurrentTheme
    {
        get => _currentTheme;
        private set => this.RaiseAndSetIfChanged(ref _currentTheme, value);
    }

    /// <summary>The variant actually shown (Dark or Light), resolving "System".</summary>
    public ThemeVariant? EffectiveVariant
    {
        get => _effectiveVariant;
        private set => this.RaiseAndSetIfChanged(ref _effectiveVariant, value);
    }

    /// <summary>Built-in and user themes.</summary>
    public ThemeCatalog Catalog { get; set; } = new();

    /// <summary>The named theme in use, or null when a plain <see cref="ThemeMode"/> is.</summary>
    public string? CurrentThemeName
    {
        get => _currentThemeName;
        private set => this.RaiseAndSetIfChanged(ref _currentThemeName, value);
    }

    /// <summary>Applies the theme, fonts and toolbar/status bar visibility from <paramref name="settings"/>.</summary>
    public void ApplySettings(AppSettings settings)
    {
        var named = string.IsNullOrWhiteSpace(settings.ThemeName) ? null : Catalog.Find(settings.ThemeName);
        if (named is not null)
            ApplyTheme(named);
        else
            Apply(settings.Theme);
        ApplyAppearance(settings);
    }

    /// <summary>
    /// Applies a named theme. The built-in Dark and Light themes are the plain palettes; any other theme
    /// loads a fresh copy of its base palette and recolours it.
    /// </summary>
    public void ApplyTheme(ThemeDefinition theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (theme.IsBuiltIn && theme.Name == ThemeCatalog.DarkName)
        {
            Apply(ThemeMode.Dark);
            CurrentThemeName = theme.Name;
            return;
        }

        if (theme.IsBuiltIn && theme.Name == ThemeCatalog.LightName)
        {
            Apply(ThemeMode.Light);
            CurrentThemeName = theme.Name;
            return;
        }

        var app = Application.Current;
        var signature = theme.Name + "|" + theme.IsDark + "|" + string.Join(";", theme.Colors.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key + "=" + c.Value));
        if (_paletteCustomized && signature == _appliedThemeSignature && app is not null && FindPalette(app) is not null)
            return; // unchanged: keep the palette (settings are re-applied on every change)

        CurrentTheme = theme.IsDark ? ThemeMode.Dark : ThemeMode.Light;
        CurrentThemeName = theme.Name;
        if (app is null)
            return;

        StopFollowingSystem(app);
        var variant = theme.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        app.RequestedThemeVariant = variant;
        var palette = SwapPalette(app, theme.IsDark ? "DarkTheme.axaml" : "LightTheme.axaml", force: true);
        _paletteCustomized = true;
        foreach (var (key, value) in theme.Colors)
            SetPaletteColor(palette, key, value);
        _appliedThemeSignature = signature;
        EffectiveVariant = variant;
    }

    /// <summary>Changes one palette colour of the active palette immediately (theme editor preview).</summary>
    public bool PreviewColor(string key, string value)
    {
        var app = Application.Current;
        if (app is null || !ThemeDefinition.IsValidColor(value))
            return false;

        var palette = FindPalette(app);
        if (palette is null)
            return false;

        if (!_paletteCustomized)
        {
            // Never recolour the shared default palette: switch to a private copy first.
            var isDark = EffectiveVariant != ThemeVariant.Light;
            palette = SwapPalette(app, isDark ? "DarkTheme.axaml" : "LightTheme.axaml", force: true);
            _paletteCustomized = true;
        }

        _appliedThemeSignature = null;
        return SetPaletteColor(palette, key, value);
    }

    public void Apply(ThemeMode theme)
    {
        CurrentTheme = theme;
        CurrentThemeName = null;
        var app = Application.Current;
        if (app is null)
            return;

        if (theme == ThemeMode.System && !_followingSystem && app.PlatformSettings is { } platform)
        {
            platform.ColorValuesChanged += OnPlatformColorsChanged;
            _followingSystem = true;
        }
        else if (theme != ThemeMode.System)
        {
            StopFollowingSystem(app);
        }

        var variant = theme switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.System => app.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light
                ? ThemeVariant.Light
                : ThemeVariant.Dark,
            _ => ThemeVariant.Dark,
        };

        app.RequestedThemeVariant = variant;
        SwapPalette(app, variant == ThemeVariant.Light ? "LightTheme.axaml" : "DarkTheme.axaml", force: _paletteCustomized);
        _paletteCustomized = false;
        _appliedThemeSignature = null;
        EffectiveVariant = variant;
    }

    private void StopFollowingSystem(Application app)
    {
        if (_followingSystem && app.PlatformSettings is { } platformSettings)
        {
            platformSettings.ColorValuesChanged -= OnPlatformColorsChanged;
            _followingSystem = false;
        }
    }

    /// <summary>Sets the Color resource and recolours the matching "...Brush" in place.</summary>
    private static bool SetPaletteColor(StyleInclude palette, string key, string value)
    {
        if (!ThemeDefinition.IsValidColor(value) || palette.Loaded is not Styles styles)
            return false;

        var color = Color.Parse(value.Trim());
        var changed = false;
        if (styles.Resources.TryGetResource(key, null, out var existing) && existing is Color)
        {
            styles.Resources[key] = color;
            changed = true;
        }

        if (styles.Resources.TryGetResource(key + "Brush", null, out var brush) && brush is SolidColorBrush solid)
        {
            solid.Color = color;
            changed = true;
        }

        return changed;
    }

    private static StyleInclude? FindPalette(Application app) =>
        app.Styles.OfType<StyleInclude>()
            .FirstOrDefault(s => s.Source?.ToString().StartsWith(ThemeFolder, StringComparison.Ordinal) == true);

    private void OnPlatformColorsChanged(object? sender, PlatformColorValues e)
    {
        if (CurrentTheme == ThemeMode.System)
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => Apply(ThemeMode.System));
    }

    /// <summary>
    /// Replaces the mRemoteNG palette style include (Dark/Light) in the application styles and returns the
    /// active one. <paramref name="force"/> loads a fresh copy even when the same file is active.
    /// </summary>
    private static StyleInclude SwapPalette(Application app, string fileName, bool force = false)
    {
        var target = new Uri(ThemeFolder + fileName);
        var index = -1;
        for (var i = 0; i < app.Styles.Count; i++)
        {
            if (app.Styles[i] is StyleInclude include
                && include.Source?.ToString().StartsWith(ThemeFolder, StringComparison.Ordinal) == true)
            {
                if (include.Source == target && !force)
                    return include; // already active
                index = i;
                break;
            }
        }

        var replacement = new StyleInclude(BaseUri) { Source = target };
        if (index >= 0)
            app.Styles[index] = replacement;
        else
            app.Styles.Add(replacement);
        return replacement;
    }

    private void ApplyAppearance(AppSettings settings)
    {
        var app = Application.Current;
        if (app is null)
            return;

        var styles = new Styles();
        var defaults = new AppSettings();

        if (!string.IsNullOrWhiteSpace(settings.FontFamily))
        {
            var family = new FontFamily(settings.FontFamily);
            styles.Add(Setter<TextBlock>(TextBlock.FontFamilyProperty, family));
            styles.Add(Setter<TemplatedControl>(TemplatedControl.FontFamilyProperty, family));
        }

        // Only override sizes when the user changed them, so the default look stays as designed.
        if (Math.Abs(settings.FontSize - defaults.FontSize) > 0.01)
        {
            styles.Add(Setter<TextBlock>(TextBlock.FontSizeProperty, settings.FontSize));
            styles.Add(Setter<TemplatedControl>(TemplatedControl.FontSizeProperty, settings.FontSize));
        }

        if (_appearanceStyles is not null)
            app.Styles.Remove(_appearanceStyles);
        _appearanceStyles = styles;
        app.Styles.Add(styles);

        ApplyBarVisibility(app, settings);
    }

    private static Style Setter<T>(AvaloniaProperty property, object value) where T : StyledElement =>
        new(x => x.Is<T>()) { Setters = { new Setter(property, value) } };

    /// <summary>Shows/hides the bars (Border.toolbar / Border.statusbar) directly inside the main window's root panel.</summary>
    private static void ApplyBarVisibility(Application app, AppSettings settings)
    {
        if (app.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: Views.MainWindow { Content: Panel root } })
            return;

        foreach (var bar in root.Children.OfType<Border>())
        {
            if (bar.Classes.Contains("toolbar"))
                bar.IsVisible = settings.ShowToolbar;
            else if (bar.Classes.Contains("statusbar"))
                bar.IsVisible = settings.ShowStatusBar;
        }
    }
}
