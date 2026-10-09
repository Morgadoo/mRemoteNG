using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using mRemoteNG.Avalonia.Services;

namespace mRemoteNG.Avalonia.Controls;

/// <summary>
/// Small protocol chip ("SSH", "RDP"…): the protocol colour at 18 % as background and the full colour as text
/// (docs/design-system.md §2, §6). <see cref="Protocol"/> takes the Core or Protocols <c>ProtocolType</c> or a name;
/// <see cref="Text"/> overrides the label. Template: Themes/Controls.axaml.
/// <code>&lt;ctl:ProtocolChip Protocol="{Binding Protocol}"/&gt;</code>
/// </summary>
public class ProtocolChip : TemplatedControl
{
    public static readonly StyledProperty<object?> ProtocolProperty =
        AvaloniaProperty.Register<ProtocolChip, object?>(nameof(Protocol));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ProtocolChip, string?>(nameof(Text));

    public static readonly DirectProperty<ProtocolChip, string> LabelProperty =
        AvaloniaProperty.RegisterDirect<ProtocolChip, string>(nameof(Label), c => c.Label);

    public static readonly DirectProperty<ProtocolChip, IBrush> ProtocolBrushProperty =
        AvaloniaProperty.RegisterDirect<ProtocolChip, IBrush>(nameof(ProtocolBrush), c => c.ProtocolBrush);

    public static readonly DirectProperty<ProtocolChip, IBrush> TintBrushProperty =
        AvaloniaProperty.RegisterDirect<ProtocolChip, IBrush>(nameof(TintBrush), c => c.TintBrush);

    private string _label = string.Empty;
    private IBrush _protocolBrush = ProtocolVisuals.BrushFor(null);
    private IBrush _tintBrush = ProtocolVisuals.TintBrushFor(null);

    public object? Protocol
    {
        get => GetValue(ProtocolProperty);
        set => SetValue(ProtocolProperty, value);
    }

    /// <summary>Optional label; the protocol's short name when empty.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Label
    {
        get => _label;
        private set => SetAndRaise(LabelProperty, ref _label, value);
    }

    public IBrush ProtocolBrush
    {
        get => _protocolBrush;
        private set => SetAndRaise(ProtocolBrushProperty, ref _protocolBrush, value);
    }

    public IBrush TintBrush
    {
        get => _tintBrush;
        private set => SetAndRaise(TintBrushProperty, ref _tintBrush, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProtocolProperty || change.Property == TextProperty)
        {
            Label = string.IsNullOrEmpty(Text) ? ProtocolVisuals.LabelFor(Protocol) : Text;
            ProtocolBrush = ProtocolVisuals.BrushFor(Protocol);
            TintBrush = ProtocolVisuals.TintBrushFor(Protocol);
        }
    }
}
