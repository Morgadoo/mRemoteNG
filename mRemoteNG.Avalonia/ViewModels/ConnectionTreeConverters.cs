using Avalonia.Data.Converters;
using Avalonia.Media;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>Value converters used by the connection tree.</summary>
public static class ConnectionTreeConverters
{
    /// <summary>Read-only nodes (PuTTY sessions) are shown in italics.</summary>
    public static readonly IValueConverter ReadOnlyFontStyle =
        new FuncValueConverter<bool, FontStyle>(readOnly => readOnly ? FontStyle.Italic : FontStyle.Normal);
}
