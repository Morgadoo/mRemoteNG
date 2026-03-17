namespace mRemoteNG.Platform;

/// <summary>
/// Abstracts OS-level window management operations.
/// On Windows: wraps Win32 user32.dll calls.
/// On Linux/macOS: delegates to Avalonia's window APIs.
/// </summary>
public interface IWindowService
{
    /// <summary>Moves and resizes the window identified by the native handle.</summary>
    void MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint = true);

    /// <summary>Brings the window to the foreground and gives it focus.</summary>
    bool SetForeground(IntPtr handle);

    /// <summary>Returns true if the window is currently minimized (iconic).</summary>
    bool IsMinimized(IntPtr handle);

    /// <summary>Sends a message to a window without waiting for it to be processed.</summary>
    bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Sends a message to a window and waits for the result.</summary>
    IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Shows or hides a window using standard show commands.</summary>
    bool ShowWindow(IntPtr handle, WindowShowState showState);

    /// <summary>Sets the position and z-order of a window.</summary>
    bool SetWindowPosition(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}

public enum WindowShowState
{
    Hide = 0,
    Normal = 1,
    Minimize = 2,
    Maximize = 3,
    Restore = 9,
}
