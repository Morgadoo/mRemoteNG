using Avalonia;
using Avalonia.Styling;
using mRemoteNG.Platform;
using ReactiveUI;

namespace mRemoteNG.Avalonia.Services;

public enum AppTheme { Dark, Light, System }

/// <summary>
/// Manages Avalonia theme switching between Dark, Light, and System-follow modes.
/// Persists the selected theme via ISettingsProvider.
/// </summary>
public sealed class ThemeService : ReactiveObject
{
    private AppTheme _currentTheme;

    public static ThemeService Instance { get; } = new();

    public AppTheme CurrentTheme
    {
        get => _currentTheme;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentTheme, value);
            Apply(value);
        }
    }

    private ThemeService()
    {
        // Default to dark theme (matches original mRemoteNG VS2015 Dark)
        _currentTheme = AppTheme.Dark;
    }

    public void Apply(AppTheme theme)
    {
        if (Application.Current is null) return;

        Application.Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.System => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };
    }

    public void LoadFromSettings(ISettingsProvider settings)
    {
        var saved = settings.GetValue("Appearance", "Theme", "Dark");
        CurrentTheme = saved switch
        {
            "Light" => AppTheme.Light,
            "System" => AppTheme.System,
            _ => AppTheme.Dark,
        };
    }

    public void SaveToSettings(ISettingsProvider settings)
    {
        settings.SetValue("Appearance", "Theme", CurrentTheme.ToString());
        settings.Save();
    }
}
