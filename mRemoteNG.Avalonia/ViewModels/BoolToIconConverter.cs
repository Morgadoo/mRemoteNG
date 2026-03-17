using System.Globalization;
using Avalonia.Data.Converters;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>
/// Converts a boolean (IsFolder) to a folder or connection icon character.
/// Uses Unicode symbols that render on all platforms via system fonts.
/// </summary>
public sealed class BoolToIconConverter : IValueConverter
{
    public static readonly BoolToIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "📁" : "🖥";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
