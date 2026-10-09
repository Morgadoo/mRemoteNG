using Avalonia;
using Avalonia.Controls;
using Material.Icons;

namespace mRemoteNG.Avalonia.Controls;

/// <summary>
/// The header of a dialog (docs/design-system.md §6 Dialogs): an optional 20 px icon on a tinted tile, an <c>h1</c>
/// title and a <c>caption</c> subtitle; <see cref="ContentControl.Content"/> is shown on the right (e.g. a chip or a
/// button). Classes <c>danger</c>, <c>warning</c> and <c>success</c> tint the icon tile. Template: Themes/Dialogs.axaml.
/// <code>&lt;ctl:DialogHeader Icon="LanConnect" Title="{l:Tr QuickConnect}" Subtitle="…"/&gt;</code>
/// </summary>
public class DialogHeader : ContentControl
{
    public static readonly StyledProperty<MaterialIconKind?> IconProperty =
        AvaloniaProperty.Register<DialogHeader, MaterialIconKind?>(nameof(Icon));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DialogHeader, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<DialogHeader, string?>(nameof(Subtitle));

    public static readonly DirectProperty<DialogHeader, MaterialIconKind> IconKindProperty =
        AvaloniaProperty.RegisterDirect<DialogHeader, MaterialIconKind>(nameof(IconKind), h => h.IconKind);

    public static readonly DirectProperty<DialogHeader, bool> HasIconProperty =
        AvaloniaProperty.RegisterDirect<DialogHeader, bool>(nameof(HasIcon), h => h.HasIcon);

    public static readonly DirectProperty<DialogHeader, bool> HasSubtitleProperty =
        AvaloniaProperty.RegisterDirect<DialogHeader, bool>(nameof(HasSubtitle), h => h.HasSubtitle);

    private MaterialIconKind _iconKind;
    private bool _hasIcon;
    private bool _hasSubtitle;

    /// <summary>Material glyph shown before the title; none when null.</summary>
    public MaterialIconKind? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public MaterialIconKind IconKind
    {
        get => _iconKind;
        private set => SetAndRaise(IconKindProperty, ref _iconKind, value);
    }

    public bool HasIcon
    {
        get => _hasIcon;
        private set => SetAndRaise(HasIconProperty, ref _hasIcon, value);
    }

    public bool HasSubtitle
    {
        get => _hasSubtitle;
        private set => SetAndRaise(HasSubtitleProperty, ref _hasSubtitle, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty)
        {
            HasIcon = Icon is not null;
            IconKind = Icon ?? default;
        }
        else if (change.Property == SubtitleProperty)
        {
            HasSubtitle = !string.IsNullOrEmpty(Subtitle);
        }
    }
}
