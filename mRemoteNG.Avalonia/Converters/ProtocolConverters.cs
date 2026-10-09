using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Material.Icons;
using mRemoteNG.Avalonia.Services;

namespace mRemoteNG.Avalonia.Converters;

/// <summary>
/// Protocol → icon / colour / label converters for XAML (docs/design-system.md §5). The input may be the Core
/// <c>ProtocolType</c>, the Protocols <c>ProtocolType</c> or a protocol name. Usage:
/// <code>
/// xmlns:conv="using:mRemoteNG.Avalonia.Converters"
/// &lt;mi:MaterialIcon Kind="{Binding Protocol, Converter={x:Static conv:ProtocolConverters.Icon}}"
///                  Foreground="{Binding Protocol, Converter={x:Static conv:ProtocolConverters.Brush}}"/&gt;
/// </code>
/// </summary>
public static class ProtocolConverters
{
    /// <summary>Protocol → <see cref="MaterialIconKind"/>.</summary>
    public static IValueConverter Icon { get; } = new FuncValueConverter<object?, MaterialIconKind>(ProtocolVisuals.IconFor);

    /// <summary>Protocol → protocol colour brush (icons, chip text).</summary>
    public static IValueConverter Brush { get; } = new FuncValueConverter<object?, IBrush>(ProtocolVisuals.BrushFor);

    /// <summary>Protocol → protocol colour at 18 % (chip background).</summary>
    public static IValueConverter TintBrush { get; } = new FuncValueConverter<object?, IBrush>(ProtocolVisuals.TintBrushFor);

    /// <summary>Protocol → short label ("SSH", "RDP", "HTTPS"…).</summary>
    public static IValueConverter Label { get; } = new FuncValueConverter<object?, string>(ProtocolVisuals.LabelFor);
}

/// <summary>
/// Connection / tree node → icon and icon colour (docs/design-system.md §5). The input may be a Core
/// <c>ConnectionInfo</c>, a <c>ConnectionNodeViewModel</c> or a <c>SessionTabViewModel</c>. Usage:
/// <code>
/// &lt;mi:MaterialIcon Foreground="{Binding Converter={x:Static conv:ConnectionConverters.Brush}}"&gt;
///   &lt;mi:MaterialIcon.Kind&gt;
///     &lt;MultiBinding Converter="{x:Static conv:ConnectionConverters.IconExpanded}"&gt;
///       &lt;Binding/&gt;&lt;Binding Path="IsExpanded"/&gt;
///     &lt;/MultiBinding&gt;
///   &lt;/mi:MaterialIcon.Kind&gt;
/// &lt;/mi:MaterialIcon&gt;
/// </code>
/// </summary>
public static class ConnectionConverters
{
    /// <summary>Connection → <see cref="MaterialIconKind"/> (folders follow their expansion at conversion time).</summary>
    public static IValueConverter Icon { get; } =
        new FuncValueConverter<object?, MaterialIconKind>(c => ProtocolVisuals.IconForConnection(c));

    /// <summary>[connection, isExpanded] → <see cref="MaterialIconKind"/>; re-evaluates when a folder opens or closes.</summary>
    public static IMultiValueConverter IconExpanded { get; } = new ConnectionIconMultiConverter();

    /// <summary>
    /// Connection → protocol colour; folders and roots return <see cref="AvaloniaProperty.UnsetValue"/> so the icon
    /// keeps the inherited text colour.
    /// </summary>
    public static IValueConverter Brush { get; } = new ConnectionBrushConverter();

    private sealed class ConnectionIconMultiConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            var connection = values.Count > 0 ? values[0] : null;
            bool? expanded = values.Count > 1 && values[1] is bool b ? b : null;
            return ProtocolVisuals.IconForConnection(connection, expanded);
        }
    }

    private sealed class ConnectionBrushConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            ProtocolVisuals.BrushForConnection(value) ?? AvaloniaProperty.UnsetValue;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
