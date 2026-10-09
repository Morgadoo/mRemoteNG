using Avalonia.Data.Converters;
using Avalonia.Media;
using mRemoteNG.Core.Localization;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>Value converters used by the connection tree.</summary>
public static class ConnectionTreeConverters
{
    /// <summary>Read-only nodes (PuTTY sessions) are shown in italics.</summary>
    public static readonly IValueConverter ReadOnlyFontStyle =
        new FuncValueConverter<bool, FontStyle>(readOnly => readOnly ? FontStyle.Italic : FontStyle.Normal);

    /// <summary>Tooltip of the open-session dot on a row.</summary>
    public static readonly IValueConverter SessionIndicatorToolTip =
        new FuncValueConverter<SessionIndicator, string?>(indicator => indicator switch
        {
            SessionIndicator.Connected => Localizer.Get("SessionDotConnected"),
            SessionIndicator.Busy => Localizer.Get("SessionDotBusy"),
            SessionIndicator.Failed => Localizer.Get("SessionDotFailed"),
            SessionIndicator.Idle => Localizer.Get("SessionDotIdle"),
            _ => null,
        });
}
