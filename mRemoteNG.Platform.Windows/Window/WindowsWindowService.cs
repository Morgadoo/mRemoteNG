using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace mRemoteNG.Platform.Windows.Window;

/// <summary>
/// Windows Win32 implementation of <see cref="IWindowService"/>.
/// All P/Invoke declarations are kept internal to this class.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWindowService : IWindowService
{
    public void MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint = true) =>
        NativeMethods.MoveWindow(handle, x, y, width, height, repaint);

    public bool SetForeground(IntPtr handle) =>
        NativeMethods.SetForegroundWindow(handle);

    public bool IsMinimized(IntPtr handle) =>
        NativeMethods.IsIconic(handle);

    public bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam) =>
        NativeMethods.PostMessage(handle, message, wParam, lParam);

    public IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam) =>
        NativeMethods.SendMessage(handle, message, wParam, lParam);

    public bool ShowWindow(IntPtr handle, WindowShowState showState) =>
        NativeMethods.ShowWindow(handle, (int)showState);

    public bool SetWindowPosition(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags) =>
        NativeMethods.SetWindowPos(handle, insertAfter, x, y, width, height, flags);

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        internal static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    }
}
