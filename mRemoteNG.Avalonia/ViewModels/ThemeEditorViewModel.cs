using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Media;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Core.Localization;
using mRemoteNG.Core.Settings;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>One editable palette colour.</summary>
public sealed class ThemeColorEntry : ReactiveObject
{
    private readonly Action<ThemeColorEntry> _changed;
    private string _value;

    public ThemeColorEntry(string key, string description, string value, Action<ThemeColorEntry> changed)
    {
        Key = key;
        Description = description;
        _value = value;
        _changed = changed;
    }

    public string Key { get; }

    public string Description { get; }

    /// <summary>"#rrggbb"; invalid text is kept but not applied.</summary>
    public string Value
    {
        get => _value;
        set
        {
            value = value?.Trim() ?? string.Empty;
            if (value == _value)
                return;
            this.RaiseAndSetIfChanged(ref _value, value);
            this.RaisePropertyChanged(nameof(IsValid));
            this.RaisePropertyChanged(nameof(Swatch));
            if (IsValid)
                _changed(this);
        }
    }

    public bool IsValid => ThemeDefinition.IsValidColor(Value);

    public IBrush Swatch => IsValid ? new SolidColorBrush(Color.Parse(Value)) : Brushes.Transparent;
}

/// <summary>
/// Theme editor (legacy ThemePage): pick a theme to start from, change its colours (previewed live in the
/// whole application), save it under a new name as a user theme (JSON in the settings folder's Themes
/// directory) and apply it, or delete a user theme. Closing without saving restores the active theme.
/// </summary>
public sealed class ThemeEditorViewModel : ReactiveObject
{
    private readonly ThemeCatalog _catalog;
    private readonly ThemeService _themes;
    private readonly AppSettingsService? _settings;
    private ThemeDefinition? _baseTheme;
    private string _name = string.Empty;
    private string _status = string.Empty;
    private IReadOnlyList<ThemeDefinition> _allThemes = [];

    public ThemeEditorViewModel(ThemeService themes, AppSettingsService? settings, string? startFrom = null)
    {
        _themes = themes;
        _catalog = themes.Catalog;
        _settings = settings;
        SaveCommand = ReactiveCommand.Create(() => { Save(); });
        DeleteCommand = ReactiveCommand.Create(Delete, this.WhenAnyValue(x => x.CanDelete));
        Reload(startFrom ?? themes.CurrentThemeName ?? (themes.EffectiveVariant == global::Avalonia.Styling.ThemeVariant.Light
            ? ThemeCatalog.LightName
            : ThemeCatalog.DarkName));
    }

    /// <summary>Every theme that can be copied.</summary>
    public IReadOnlyList<ThemeDefinition> AllThemes
    {
        get => _allThemes;
        private set => this.RaiseAndSetIfChanged(ref _allThemes, value);
    }

    /// <summary>The theme being edited / copied; selecting one loads its colours.</summary>
    public ThemeDefinition? BaseTheme
    {
        get => _baseTheme;
        set
        {
            if (value is null || ReferenceEquals(value, _baseTheme))
                return;
            this.RaiseAndSetIfChanged(ref _baseTheme, value);
            LoadColors(value);
            Name = value.IsBuiltIn ? Localizer.Format("CustomThemeNameFormat", value.Name) : value.Name;
            this.RaisePropertyChanged(nameof(CanDelete));
            this.RaisePropertyChanged(nameof(IsDark));
            _themes.ApplyTheme(value);
        }
    }

    public bool IsDark => _baseTheme?.IsDark ?? true;

    public ObservableCollection<ThemeColorEntry> Colors { get; } = [];

    /// <summary>Name the theme is saved under.</summary>
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value ?? string.Empty);
    }

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool CanDelete => _baseTheme is { IsBuiltIn: false };

    /// <summary>The theme saved by the last <see cref="Save"/> (applied and selected in the settings).</summary>
    public string? SavedThemeName { get; private set; }

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }

    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    /// <summary>The definition the editor would save now.</summary>
    public ThemeDefinition BuildTheme() => new()
    {
        Name = Name.Trim(),
        IsDark = IsDark,
        Colors = Colors.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal),
    };

    /// <summary>Saves as a user theme, applies it and makes it the selected theme. Returns false on validation errors.</summary>
    public bool Save()
    {
        var theme = BuildTheme();
        var errors = _catalog.Validate(theme);
        if (errors.Count > 0)
        {
            Status = string.Join(" ", errors);
            return false;
        }

        try
        {
            var path = _catalog.Save(theme);
            SavedThemeName = theme.Name;
            _settings?.Update(s => s.ThemeName = theme.Name);
            _themes.ApplyTheme(theme);
            Reload(theme.Name);
            Status = Localizer.Format("ThemeSavedFormat", theme.Name, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status = Localizer.Format("CouldNotSaveThemeFormat", ex.Message);
            return false;
        }
    }

    private void Delete()
    {
        if (_baseTheme is not { IsBuiltIn: false } theme)
            return;

        _catalog.Delete(theme.Name);
        if (_settings is not null && string.Equals(_settings.Current.ThemeName, theme.Name, StringComparison.OrdinalIgnoreCase))
            _settings.Update(s => s.ThemeName = string.Empty);
        Status = Localizer.Format("ThemeDeletedFormat", theme.Name);
        Reload(theme.IsDark ? ThemeCatalog.DarkName : ThemeCatalog.LightName);
    }

    /// <summary>Restores the theme from the settings (closing the editor without saving).</summary>
    public void RevertPreview()
    {
        if (_settings is not null)
            _themes.ApplySettings(_settings.Current);
    }

    private void Reload(string selectName)
    {
        AllThemes = _catalog.GetAll();
        _baseTheme = null;
        BaseTheme = AllThemes.FirstOrDefault(t => string.Equals(t.Name, selectName, StringComparison.OrdinalIgnoreCase))
                    ?? AllThemes[0];
    }

    private void LoadColors(ThemeDefinition theme)
    {
        Colors.Clear();
        var fallback = theme.IsDark ? ThemeCatalog.Dark : ThemeCatalog.Light;
        foreach (var (key, description) in ThemeDefinition.PaletteKeys)
        {
            var value = theme.GetColor(key) ?? fallback.GetColor(key) ?? "#000000";
            Colors.Add(new ThemeColorEntry(key, description, value, OnColorChanged));
        }
    }

    private void OnColorChanged(ThemeColorEntry entry) => _themes.PreviewColor(entry.Key, entry.Value);
}
