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

    /// <summary>Applies the theme, fonts and toolbar/status bar visibility from <paramref name="settings"/>.</summary>
    public void ApplySettings(AppSettings settings)
    {
        Apply(settings.Theme);
        ApplyAppearance(settings);
    }

    public void Apply(ThemeMode theme)
    {
        CurrentTheme = theme;
        var app = Application.Current;
        if (app is null)
            return;

        if (theme == ThemeMode.System && !_followingSystem && app.PlatformSettings is { } platform)
        {
            platform.ColorValuesChanged += OnPlatformColorsChanged;
            _followingSystem = true;
        }
        else if (theme != ThemeMode.System && _followingSystem && app.PlatformSettings is { } platformSettings)
        {
            platformSettings.ColorValuesChanged -= OnPlatformColorsChanged;
            _followingSystem = false;
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
        SwapPalette(app, variant == ThemeVariant.Light ? "LightTheme.axaml" : "DarkTheme.axaml");
        EffectiveVariant = variant;
    }

    private void OnPlatformColorsChanged(object? sender, PlatformColorValues e)
    {
        if (CurrentTheme == ThemeMode.System)
            global::Avalonia.Threading.Dispatcher.UIThread.Post(() => Apply(ThemeMode.System));
    }

    /// <summary>Replaces the mRemoteNG palette style include (Dark/Light) in the application styles.</summary>
    private static void SwapPalette(Application app, string fileName)
    {
        var target = new Uri(ThemeFolder + fileName);
        var index = -1;
        for (var i = 0; i < app.Styles.Count; i++)
        {
            if (app.Styles[i] is StyleInclude include
                && include.Source?.ToString().StartsWith(ThemeFolder, StringComparison.Ordinal) == true)
            {
                if (include.Source == target)
                    return; // already active
                index = i;
                break;
            }
        }

        var replacement = new StyleInclude(BaseUri) { Source = target };
        if (index >= 0)
            app.Styles[index] = replacement;
        else
            app.Styles.Add(replacement);
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
