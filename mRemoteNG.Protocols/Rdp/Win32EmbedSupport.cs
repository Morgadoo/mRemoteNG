using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace mRemoteNG.Protocols.Rdp;

/// <summary>
/// Windows glue for a wfreerdp window created as a child of our HWND (<c>/parent-window:&lt;HWND&gt;</c>).
/// wfreerdp does not track its parent's size, so the child is resized from the host control's size
/// changes. Child windows of another process share the parent's input queue (Windows attaches the
/// threads' input automatically), which is what makes <c>SetFocus</c> on the child legal.
/// NOTE: this path has not been exercised on a Windows machine yet (development happened on Linux).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Win32EmbedSupport : IEmbeddedWindowSupport
{
    private readonly nint _parent;
    private bool _focusPending;

    public Win32EmbedSupport(nint parent) => _parent = parent;

    public event EventHandler? RemoteWindowMapped { add { } remove { } }

    public nint RemoteWindow => GetWindow(_parent, GW_CHILD);

    public bool RemoteHasFocus
    {
        get
        {
            nint child = RemoteWindow;
            return child != 0 && GetFocus() == child;
        }
    }

    public void FocusRemote()
    {
        nint child = RemoteWindow;
        if (child != 0 && IsWindowVisible(child))
        {
            SetFocus(child);
            _focusPending = false;
        }
        else
        {
            _focusPending = true;
        }
    }

    public void ReturnFocusTo(nint topLevel)
    {
        _focusPending = false;
        if (RemoteHasFocus && topLevel != 0)
            SetFocus(topLevel);
    }

    public void ResizeRemote(int width, int height)
    {
        nint child = RemoteWindow;
        if (child == 0) return;
        MoveWindow(child, 0, 0, Math.Max(1, width), Math.Max(1, height), true);
        if (_focusPending) FocusRemote();
    }

    public void Dispose()
    {
    }

    private const uint GW_CHILD = 5;

    [DllImport("user32.dll")] private static extern nint GetWindow(nint hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern nint GetFocus();
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(nint hWnd, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);
}
