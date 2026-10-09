using System.Globalization;
using Avalonia.Data.Converters;

namespace mRemoteNG.Avalonia.Views.Sessions;

/// <summary>Small value converters of the session views.</summary>
public static class SessionConverters
{
    /// <summary>Text in capitals (overline headings such as panel names).</summary>
    public static IValueConverter Upper { get; } =
        new FuncValueConverter<string?, string>(text => text?.ToUpper(CultureInfo.CurrentCulture) ?? string.Empty);

    /// <summary>True for a number greater than zero (counts shown only when there is something to count).</summary>
    public static IValueConverter IsPositive { get; } = new FuncValueConverter<int, bool>(count => count > 0);
}
