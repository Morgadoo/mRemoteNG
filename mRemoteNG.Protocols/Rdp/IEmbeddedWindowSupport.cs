using Avalonia.Platform;
using Microsoft.Extensions.Logging;

namespace mRemoteNG.Protocols.Rdp;

/// <summary>
/// Platform glue for a FreeRDP window living inside our native parent window (/parent-window).
/// </summary>
internal interface IEmbeddedWindowSupport : IDisposable
{
    /// <summary>Raised (on a background thread) when FreeRDP maps its window inside the parent, where detectable.</summary>
    event EventHandler? RemoteWindowMapped;

    /// <summary>The FreeRDP window inside the parent, or 0 when it does not exist (yet).</summary>
    nint RemoteWindow { get; }

    /// <summary>True when the FreeRDP window currently has the keyboard focus.</summary>
    bool RemoteHasFocus { get; }

    /// <summary>Gives the keyboard focus to the FreeRDP window (now, or as soon as it is shown).</summary>
    void FocusRemote();

    /// <summary>If the FreeRDP window has the keyboard focus, moves it to <paramref name="topLevel"/>.</summary>
    void ReturnFocusTo(nint topLevel);

    /// <summary>Called when the host control's size (in device pixels) changes.</summary>
    void ResizeRemote(int width, int height);
}

internal static class EmbeddedWindowSupport
{
    /// <summary>Creates the glue matching the native handle type, or null when the handle cannot host FreeRDP.</summary>
    public static IEmbeddedWindowSupport? Create(IPlatformHandle parent, ILogger logger)
    {
        if (parent.HandleDescriptor == "XID" && OperatingSystem.IsLinux())
            return X11EmbedSupport.TryCreate(parent.Handle, logger);
        if (parent.HandleDescriptor == "HWND" && OperatingSystem.IsWindows())
            return new Win32EmbedSupport(parent.Handle);
        return null;
    }

    /// <summary>True when FreeRDP can be parented to a native handle of this type on this OS.</summary>
    public static bool CanEmbedInto(IPlatformHandle parent) =>
        (parent.HandleDescriptor == "XID" && OperatingSystem.IsLinux())
        || (parent.HandleDescriptor == "HWND" && OperatingSystem.IsWindows());
}
