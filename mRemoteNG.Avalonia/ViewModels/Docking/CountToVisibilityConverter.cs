using System.Globalization;
using Avalonia.Data.Converters;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

public sealed class CountToVisibilityConverter : IValueConverter
{
    public static readonly CountToVisibilityConverter IsZero = new(true);
    public static readonly CountToVisibilityConverter IsNonZero = new(false);

    private readonly bool _invertWhenZero;
    private CountToVisibilityConverter(bool invertWhenZero) => _invertWhenZero = invertWhenZero;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = value is int i ? i : 0;
        return _invertWhenZero ? count == 0 : count > 0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToColorConverter : IValueConverter
{
    public static readonly BoolToColorConverter ConnectedColor = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "#4caf50" : "#888888";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
