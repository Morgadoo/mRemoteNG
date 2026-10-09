using Avalonia;
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

    /// <summary>
    /// Keeps the FreeRDP window at <paramref name="size"/> (device pixels, top-left of the parent), or makes it
    /// follow the parent's size again when null (the default).
    /// </summary>
    void SetFixedRemoteSize(PixelSize? size);

    /// <summary>
    /// Focuses the FreeRDP window and sends it a key combination: the keys are pressed in order and released
    /// in reverse order. Returns false when the window does not exist or the events could not be sent.
    /// </summary>
    bool SendKeyChord(IReadOnlyList<ChordKey> keys);

    /// <summary>Time since the user's last keyboard/pointer input on this desktop, or null when unknown.</summary>
    TimeSpan? UserIdleTime { get; }

    /// <summary>True when the pointer is over the FreeRDP window.</summary>
    bool PointerOverRemote { get; }
}

/// <summary>Keys used in the key combinations <see cref="IEmbeddedWindowSupport.SendKeyChord"/> sends.</summary>
internal enum ChordKey
{
    Control,
    Alt,
    Delete,
    Escape,
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
