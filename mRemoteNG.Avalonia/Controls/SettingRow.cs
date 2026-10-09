using Avalonia;
using Avalonia.Controls;

namespace mRemoteNG.Avalonia.Controls;

/// <summary>
/// One settings row (docs/design-system.md §6 Cards and settings): <see cref="Header"/> (13, TextPrimary) and an
/// optional <see cref="Description"/> (<c>caption</c>) on the left, the control (<see cref="ContentControl.Content"/>)
/// on the right. Class <c>stacked</c> puts a wide control under the text. Rows inside a
/// <c>Border.settings</c> card are separated by dividers. Template: Themes/Dialogs.axaml.
/// <code>&lt;ctl:SettingRow Header="{l:Tr Theme}" Description="…"&gt;&lt;ComboBox …/&gt;&lt;/ctl:SettingRow&gt;</code>
/// </summary>
public class SettingRow : ContentControl
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Header));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingRow, string?>(nameof(Description));

    public static readonly DirectProperty<SettingRow, bool> HasDescriptionProperty =
        AvaloniaProperty.RegisterDirect<SettingRow, bool>(nameof(HasDescription), r => r.HasDescription);

    private bool _hasDescription;

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public bool HasDescription
    {
        get => _hasDescription;
        private set => SetAndRaise(HasDescriptionProperty, ref _hasDescription, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DescriptionProperty)
            HasDescription = !string.IsNullOrWhiteSpace(Description);
    }
}
