namespace mRemoteNG.Platform.Linux;

/// <summary>
/// No-op window service for Linux and macOS.
/// Window management on these platforms is handled entirely by Avalonia;
/// there is no equivalent of Win32 window handles for application-level manipulation.
/// </summary>
public sealed class NullWindowService : IWindowService
{
    public void MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint = true) { }
    public bool SetForeground(IntPtr handle) => true;
    public bool IsMinimized(IntPtr handle) => false;
    public bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam) => false;
    public IntPtr SendMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam) => IntPtr.Zero;
    public bool ShowWindow(IntPtr handle, WindowShowState showState) => true;
    public bool SetWindowPosition(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags) => true;
}
