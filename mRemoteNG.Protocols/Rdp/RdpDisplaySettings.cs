using System.Globalization;
using Avalonia;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.Rdp;

/// <summary>The legacy RDP "Resolution" choices.</summary>
internal enum RdpResolutionMode
{
    /// <summary>The remote desktop has the size of the tab (and follows it with automatic resize).</summary>
    FitToWindow,

    /// <summary>The remote desktop is created at the tab's size and then scaled to the tab.</summary>
    SmartSize,

    /// <summary>FreeRDP runs full screen in its own window.</summary>
    Fullscreen,

    /// <summary>A fixed desktop size (<see cref="RdpDisplaySettings.FixedSize"/>).</summary>
    Fixed,
}

/// <summary>
/// Display settings of an RDP connection (Extras <see cref="ConnectionParametersFactory.Keys.RdpResolution"/> and
/// <see cref="ConnectionParametersFactory.Keys.RdpAutoResize"/>) and how they translate to FreeRDP.
///
/// FreeRDP either resizes the remote desktop with the window (<c>/dynamic-resolution</c>) or scales the
/// desktop to its window (<c>/smart-sizing</c>); it refuses both at once. Sessions in a tab that do not
/// resize the remote desktop are therefore always started with smart sizing: the embedding glue keeps
/// FreeRDP's window at exactly the desktop size (scale 1:1) while smart sizing is "off", and makes it follow
/// the tab (scaled) while it is "on". That is what makes smart sizing switchable while connected.
/// </summary>
internal sealed record RdpDisplaySettings(RdpResolutionMode Mode, PixelSize? FixedSize, bool AutoResize)
{
    public static RdpDisplaySettings Default { get; } = new(RdpResolutionMode.FitToWindow, null, true);

    public static RdpDisplaySettings From(ConnectionParameters parameters)
    {
        var extras = parameters.Extras;
        bool autoResize = !extras.TryGetValue(ConnectionParametersFactory.Keys.RdpAutoResize, out string? auto)
                          || !auto.Equals("false", StringComparison.OrdinalIgnoreCase);
        string value = extras.GetValueOrDefault(ConnectionParametersFactory.Keys.RdpResolution, "fit").Trim();

        if (value.Equals("smartsize", StringComparison.OrdinalIgnoreCase))
            return new(RdpResolutionMode.SmartSize, null, autoResize);
        if (value.Equals("fullscreen", StringComparison.OrdinalIgnoreCase))
            return new(RdpResolutionMode.Fullscreen, null, autoResize);
        if (TryParseSize(value) is { } size)
            return new(RdpResolutionMode.Fixed, size, autoResize);
        return new(RdpResolutionMode.FitToWindow, null, autoResize);
    }

    /// <summary>Parses "1024x768"; null for anything else (FreeRDP accepts at most 8192 per side).</summary>
    internal static PixelSize? TryParseSize(string value)
    {
        int x = value.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        if (x <= 0) return null;
        if (!int.TryParse(value.AsSpan(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(value.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int height))
            return null;
        return width is >= 200 and <= 8192 && height is >= 200 and <= 8192 ? new PixelSize(width, height) : null;
    }

    /// <summary>Full screen always runs in FreeRDP's own window; everything else can live in the tab.</summary>
    public bool CanEmbed => Mode != RdpResolutionMode.Fullscreen;

    /// <summary>The server resizes the remote desktop whenever FreeRDP's window is resized.</summary>
    public bool DynamicResolution => AutoResize && Mode is RdpResolutionMode.FitToWindow or RdpResolutionMode.Fullscreen;

    /// <summary>FreeRDP is started with <c>/smart-sizing</c>.</summary>
    public bool UsesSmartSizing(bool embedded) => embedded ? !DynamicResolution : Mode == RdpResolutionMode.SmartSize;

    /// <summary>The remote desktop size requested for a session embedded in a tab of <paramref name="tabSize"/>.</summary>
    public PixelSize EmbeddedDesktopSize(PixelSize tabSize) => Mode == RdpResolutionMode.Fixed && FixedSize is { } size ? size : tabSize;

    /// <summary>Whether an embedded session starts scaled to the tab (smart sizing "on").</summary>
    public bool StartsScaled => Mode == RdpResolutionMode.SmartSize;
}
