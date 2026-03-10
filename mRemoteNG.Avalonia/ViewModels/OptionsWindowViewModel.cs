using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the Options/Settings dialog.
/// Owns the list of settings categories and the currently selected page.
/// </summary>
public sealed class OptionsWindowViewModel : ReactiveObject
{
    private SettingsCategoryViewModel? _selectedCategory;

    public List<SettingsCategoryViewModel> Categories { get; } =
    [
        new("Appearance", "appearance"),
        new("Connections", "connections"),
        new("Tabs & Panels", "tabs"),
        new("Security", "security"),
        new("Advanced", "advanced"),
        new("Updates", "updates"),
        new("Notifications", "notifications"),
        new("Credentials", "credentials"),
        new("Theme", "theme"),
        new("Protocols", "protocols"),
    ];

    public SettingsCategoryViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set => this.RaiseAndSetIfChanged(ref _selectedCategory, value);
    }

    public OptionsWindowViewModel() =>
        SelectedCategory = Categories.FirstOrDefault();
}

public sealed class SettingsCategoryViewModel(string displayName, string key) : ReactiveObject
{
    public string DisplayName { get; } = displayName;
    public string Key { get; } = key;
}
