using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace mRemoteNG.Protocols.Embedding;

/// <summary>
/// Windows counterpart of <see cref="X11ForeignWindows"/>, following the legacy IntegratedProgram: wait for the
/// process's main window, make it a borderless child of our HWND (SetParent) and fill the host.
/// NOTE: written against the Win32 documentation and the legacy code; not yet exercised on a Windows machine.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32ForeignWindows
{
    public static async Task<nint> WaitForMainWindowAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            process.WaitForInputIdle((int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
        }
        catch (InvalidOperationException)
        {
            // No message loop (console program) or already exited.
        }

        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            process.Refresh();
            if (process.HasExited)
                return 0;
            // Same filter as the legacy code: the IME helper window is not the program's window.
            if (process.MainWindowHandle != 0 && process.MainWindowTitle != "Default IME")
                return process.MainWindowHandle;
            if (DateTime.UtcNow >= deadline)
                return 0;
            await Task.Delay(100, ct);
        }
    }

    public static bool Reparent(nint window, nint parent, int width, int height)
    {
        long style = GetWindowLongPtr(window, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_POPUP | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        style |= WS_CHILD;
        SetWindowLongPtr(window, GWL_STYLE, new nint(style));
        if (SetParent(window, parent) == 0)
            return false;
        MoveWindow(window, 0, 0, Math.Max(1, width), Math.Max(1, height), true);
        ShowWindow(window, SW_SHOW);
        return true;
    }

    private const int GWL_STYLE = -16;
    private const int SW_SHOW = 5;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_SYSMENU = 0x00080000L;
    private const long WS_MINIMIZEBOX = 0x00020000L;
    private const long WS_MAXIMIZEBOX = 0x00010000L;

    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint newParent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hWnd, int index, nint value);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(nint hWnd, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);
}
