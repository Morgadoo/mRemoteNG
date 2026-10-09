using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;

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
    private PixelSize? _fixedSize;

    public Win32EmbedSupport(nint parent) => _parent = parent;

    public event EventHandler? RemoteWindowMapped { add { } remove { } }

    public nint RemoteWindow => GetWindow(_parent, GW_CHILD);

    public bool RemoteHasFocus
    {
        get
        {
            nint child = RemoteWindow;
            return child != 0 && FocusedWindow() == child;
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
        if (_fixedSize is { } size)
            MoveWindow(child, 0, 0, size.Width, size.Height, true);
        else
            MoveWindow(child, 0, 0, Math.Max(1, width), Math.Max(1, height), true);
        if (_focusPending) FocusRemote();
    }

    public void SetFixedRemoteSize(PixelSize? size)
    {
        _fixedSize = size;
        if (size is null && GetClientRect(_parent, out Rect rect))
            ResizeRemote(rect.Right - rect.Left, rect.Bottom - rect.Top);
        else
            ResizeRemote(0, 0);
    }

    public bool SendKeyChord(IReadOnlyList<ChordKey> keys)
    {
        nint child = RemoteWindow;
        if (child == 0 || !IsWindowVisible(child)) return false;
        // Injected input goes to the focused window of the foreground thread; our top-level is in the
        // foreground (the user just used our menu) and the child shares its input queue.
        SetFocus(child);

        var inputs = new Input[keys.Count * 2];
        for (int i = 0; i < keys.Count; i++)
        {
            inputs[i] = KeyInput(keys[i], up: false);
            inputs[inputs.Length - 1 - i] = KeyInput(keys[i], up: true);
        }
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length;
    }

    private static Input KeyInput(ChordKey key, bool up)
    {
        (ushort vk, bool extended) = key switch
        {
            ChordKey.Control => ((ushort)0x11, false), // VK_CONTROL
            ChordKey.Alt => ((ushort)0x12, false),     // VK_MENU
            ChordKey.Delete => ((ushort)0x2E, true),   // VK_DELETE (extended key)
            ChordKey.Escape => ((ushort)0x1B, false),  // VK_ESCAPE
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
        };
        uint flags = (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
        return new Input { Type = INPUT_KEYBOARD, Keyboard = new KeybdInput { VirtualKey = vk, Flags = flags } };
    }

    public TimeSpan? UserIdleTime
    {
        get
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return null;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
        }
    }

    public bool PointerOverRemote
    {
        get
        {
            nint child = RemoteWindow;
            if (child == 0 || !GetCursorPos(out Point point)) return false;
            nint under = WindowFromPoint(point);
            return under == child || (under != 0 && IsChild(child, under));
        }
    }

    /// <summary>The focused window of the foreground thread (GetFocus only sees the calling thread's queue).</summary>
    private static nint FocusedWindow()
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(0, ref info) ? info.Focus : 0;
    }

    public void Dispose()
    {
    }

    private const uint GW_CHILD = 5;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1;
    private const uint KEYEVENTF_KEYUP = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    /// <summary>INPUT: the union is as large as MOUSEINPUT, hence the explicit size.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeybdInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public Rect CaretRect;
    }

    [DllImport("user32.dll")] private static extern nint GetWindow(nint hWnd, uint cmd);
    [DllImport("user32.dll")] private static extern nint SetFocus(nint hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(nint parent, nint hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hWnd, out Rect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(nint hWnd, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);
}
