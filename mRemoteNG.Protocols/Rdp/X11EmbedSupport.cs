using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Microsoft.Extensions.Logging;

namespace mRemoteNG.Protocols.Rdp;

/// <summary>
/// Glue between the Avalonia-owned X11 parent window and the FreeRDP desktop window that FreeRDP
/// reparents into it (<c>/parent-window:&lt;XID&gt;</c>).
///
/// FreeRDP (verified against 3.32 xf_window.c / xf_event.c) does not follow its parent's size and only
/// takes keyboard focus itself in fullscreen mode, so this class:
///   • resizes the FreeRDP window whenever Avalonia resizes the parent (ConfigureNotify on the parent);
///     FreeRDP then sees a ConfigureNotify on its own window and, with /dynamic-resolution, asks the
///     server for the new desktop size;
///   • implements click-to-focus with a synchronous passive button grab on the parent window — the
///     classic window-manager technique: on ButtonPress we give the FreeRDP window the input focus and
///     replay the click (XAllowEvents ReplayPointer) so FreeRDP still receives it;
///   • lets the host move focus to the FreeRDP window when its tab is activated, and back to the
///     Avalonia top-level when the user clicks Avalonia UI;
///   • keeps the FreeRDP window at a fixed size instead when asked to (smart sizing switched off);
///   • sends key combinations to the FreeRDP window with XSendEvent (delivered to that window only, so the
///     local window manager never sees e.g. Ctrl+Alt+Del) and reports the user's idle time (XScreenSaver).
///
/// It uses its own Xlib connection (window ids are server-side, so any client may operate on them) and
/// a dedicated event thread; every Xlib call on that connection is made under <see cref="_lock"/>, so
/// XInitThreads is not required. X errors (e.g. FreeRDP's window vanishing between a query and a call)
/// are reported to the process-wide handler Avalonia's X11 backend installs, which only records them.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class X11EmbedSupport : IEmbeddedWindowSupport
{
    private readonly nint _display;
    private readonly nint _parent;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Thread _thread;
    private volatile bool _stopping;
    private bool _focusPending;
    private PixelSize? _fixedSize;
    private bool _idleUnavailable;
    private bool _disposed;

    public event EventHandler? RemoteWindowMapped;

    private X11EmbedSupport(nint display, nint parent, ILogger logger)
    {
        _display = display;
        _parent = parent;
        _logger = logger;

        XSelectInput(_display, _parent, StructureNotifyMask | SubstructureNotifyMask);
        XGrabButton(_display, AnyButton, AnyModifier, _parent, false, (uint)ButtonPressMask,
            GrabModeSync, GrabModeAsync, 0, 0);
        XFlush(_display);

        _thread = new Thread(EventLoop) { IsBackground = true, Name = "FreeRDP X11 embed" };
        _thread.Start();
    }

    /// <summary>Opens a second Xlib connection to $DISPLAY; returns null when that is impossible.</summary>
    public static X11EmbedSupport? TryCreate(nint parentWindow, ILogger logger)
    {
        try
        {
            nint display = XOpenDisplay(0);
            if (display == 0)
            {
                logger.LogWarning("Could not open the X display; the embedded RDP session will not follow resizes or take focus on click.");
                return null;
            }
            return new X11EmbedSupport(display, parentWindow, logger);
        }
        catch (DllNotFoundException ex)
        {
            logger.LogWarning(ex, "libX11 is not available");
            return null;
        }
    }

    /// <summary>The FreeRDP desktop window (first child of the parent), or 0 when it does not exist yet.</summary>
    public nint RemoteWindow
    {
        get
        {
            lock (_lock)
                return _disposed ? 0 : FindChild();
        }
    }

    public void FocusRemote()
    {
        lock (_lock)
        {
            if (_disposed) return;
            nint child = FindChild();
            if (child != 0 && IsViewable(child))
            {
                XSetInputFocus(_display, child, RevertToParent, CurrentTime);
                XFlush(_display);
                _focusPending = false;
            }
            else
            {
                // FreeRDP has not mapped its window yet (or the tab is hidden): focus it when it appears.
                _focusPending = true;
            }
        }
    }

    public void ReturnFocusTo(nint topLevel)
    {
        lock (_lock)
        {
            _focusPending = false;
            if (_disposed || topLevel == 0) return;
            XGetInputFocus(_display, out nint focus, out _);
            nint child = FindChild();
            if (child != 0 && (focus == child || focus == _parent))
            {
                XSetInputFocus(_display, topLevel, RevertToParent, CurrentTime);
                XFlush(_display);
            }
        }
    }

    public void ResizeRemote(int width, int height)
    {
        // Resizes are driven by ConfigureNotify on the parent (see EventLoop); nothing to do here.
    }

    public void SetFixedRemoteSize(PixelSize? size)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _fixedSize = size;
            nint child = FindChild();
            if (child == 0) return;
            if (size is { } fixedSize)
                ResizeAndRepaint(child, (uint)fixedSize.Width, (uint)fixedSize.Height);
            else if (XGetWindowAttributes(_display, _parent, out XWindowAttributes attrs) != 0)
                ResizeAndRepaint(child, (uint)Math.Max(1, attrs.Width), (uint)Math.Max(1, attrs.Height));
            XFlush(_display);
        }
    }

    public bool SendKeyChord(IReadOnlyList<ChordKey> keys)
    {
        lock (_lock)
        {
            if (_disposed) return false;
            nint child = FindChild();
            if (child == 0 || !IsViewable(child)) return false;

            var codes = new uint[keys.Count];
            for (int i = 0; i < keys.Count; i++)
            {
                codes[i] = XKeysymToKeycode(_display, KeySymOf(keys[i]));
                if (codes[i] == 0) return false;
            }

            // FreeRDP releases its pressed keys and resynchronises modifiers on FocusIn; the X server delivers
            // that FocusIn before the key events below because both come from this connection in order.
            XSetInputFocus(_display, child, RevertToParent, CurrentTime);
            nint root = XDefaultRootWindow(_display);
            uint state = 0;
            for (int i = 0; i < keys.Count; i++)
            {
                SendKey(child, root, codes[i], state, press: true);
                state |= ModifierMaskOf(keys[i]);
            }
            for (int i = keys.Count - 1; i >= 0; i--)
            {
                SendKey(child, root, codes[i], state, press: false);
                state &= ~ModifierMaskOf(keys[i]);
            }
            XFlush(_display);
            return true;
        }
    }

    // Called with _lock held. The state field is the modifier state just before the event, as for real input.
    private void SendKey(nint window, nint root, uint keycode, uint state, bool press)
    {
        var ev = new XKeyEvent
        {
            Type = press ? KeyPress : KeyRelease,
            SendEvent = 1,
            Window = window,
            Root = root,
            State = state,
            Keycode = keycode,
            SameScreen = 1,
        };
        XSendEvent(_display, window, false, press ? KeyPressMask : KeyReleaseMask, ref ev);
    }

    private static nint KeySymOf(ChordKey key) => key switch
    {
        ChordKey.Control => 0xFFE3, // XK_Control_L
        ChordKey.Alt => 0xFFE9,     // XK_Alt_L
        ChordKey.Delete => 0xFFFF,  // XK_Delete
        ChordKey.Escape => 0xFF1B,  // XK_Escape
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    private static uint ModifierMaskOf(ChordKey key) => key switch
    {
        ChordKey.Control => ControlMask,
        ChordKey.Alt => Mod1Mask,
        _ => 0,
    };

    public TimeSpan? UserIdleTime
    {
        get
        {
            lock (_lock)
            {
                if (_disposed || _idleUnavailable) return null;
                try
                {
                    if (XScreenSaverQueryExtension(_display, out _, out _)
                        && XScreenSaverQueryInfo(_display, XDefaultRootWindow(_display), out XScreenSaverInfo info) != 0)
                        return TimeSpan.FromMilliseconds(info.Idle);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    _logger.LogDebug(ex, "libXss is not available");
                }
                _idleUnavailable = true;
                return null;
            }
        }
    }

    public bool PointerOverRemote
    {
        get
        {
            lock (_lock)
            {
                if (_disposed) return false;
                nint child = FindChild();
                if (child == 0 || !IsViewable(child)) return false;
                if (!XQueryPointer(_display, child, out _, out _, out _, out _, out int x, out int y, out _))
                    return false;
                return XGetWindowAttributes(_display, child, out XWindowAttributes attrs) != 0
                       && x >= 0 && y >= 0 && x < attrs.Width && y < attrs.Height;
            }
        }
    }

    public bool RemoteHasFocus
    {
        get
        {
            lock (_lock)
            {
                if (_disposed) return false;
                XGetInputFocus(_display, out nint focus, out _);
                nint child = FindChild();
                return child != 0 && focus == child;
            }
        }
    }

    private void EventLoop()
    {
        int fd;
        lock (_lock)
            fd = XConnectionNumber(_display);

        while (!_stopping)
        {
            var pfd = new PollFd { Fd = fd, Events = PollIn };
            poll(ref pfd, 1, 100);

            lock (_lock)
            {
                if (_disposed) return;
                while (XPending(_display) > 0)
                {
                    XNextEvent(_display, out XEvent ev);
                    try
                    {
                        HandleEvent(ev);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error handling X11 event {Type}", ev.Type);
                    }
                }
            }
        }
    }

    // Called with _lock held.
    private void HandleEvent(in XEvent ev)
    {
        switch (ev.Type)
        {
            case ButtonPress:
            {
                // Our synchronous passive grab froze the pointer: always release it, then let the click
                // through to FreeRDP as if the grab did not exist.
                try
                {
                    nint child = FindChild();
                    if (child != 0 && IsViewable(child))
                        XSetInputFocus(_display, child, RevertToParent, ev.ButtonTime);
                }
                finally
                {
                    XAllowEvents(_display, ReplayPointer, ev.ButtonTime);
                    XFlush(_display);
                }
                break;
            }
            case ConfigureNotify when ev.ConfigureWindow == _parent:
                if (_fixedSize is null)
                    ResizeChildren(ev.ConfigureWidth, ev.ConfigureHeight);
                break;
            case MapNotify when ev.MapWindow != _parent && ev.MapEvent == _parent:
            {
                // FreeRDP mapped its desktop window inside our parent: match the parent's current size
                // (the user may have resized the tab since /size was computed), or the fixed size.
                if (_fixedSize is { } fixedSize)
                    XResizeWindow(_display, ev.MapWindow, (uint)fixedSize.Width, (uint)fixedSize.Height);
                else if (XGetWindowAttributes(_display, _parent, out XWindowAttributes attrs) != 0)
                    XResizeWindow(_display, ev.MapWindow, (uint)Math.Max(1, attrs.Width), (uint)Math.Max(1, attrs.Height));
                if (_focusPending)
                {
                    XSetInputFocus(_display, ev.MapWindow, RevertToParent, CurrentTime);
                    _focusPending = false;
                }
                XFlush(_display);
                RemoteWindowMapped?.Invoke(this, EventArgs.Empty);
                break;
            }
        }
    }

    // Called with _lock held. With smart sizing FreeRDP only rescales what the server redraws afterwards;
    // exposing the whole window makes it repaint the complete desktop at the new scale right away.
    private void ResizeAndRepaint(nint child, uint width, uint height)
    {
        XResizeWindow(_display, child, width, height);
        XClearArea(_display, child, 0, 0, 0, 0, true);
    }

    // Called with _lock held.
    private void ResizeChildren(int width, int height)
    {
        if (XQueryTree(_display, _parent, out _, out _, out nint children, out uint count) == 0)
            return;
        try
        {
            for (int i = 0; i < count; i++)
            {
                nint child = Marshal.ReadIntPtr(children, i * IntPtr.Size);
                ResizeAndRepaint(child, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
            }
            XFlush(_display);
        }
        finally
        {
            if (children != 0) XFree(children);
        }
    }

    // Called with _lock held.
    private nint FindChild()
    {
        if (XQueryTree(_display, _parent, out _, out _, out nint children, out uint count) == 0)
            return 0;
        try
        {
            return count > 0 ? Marshal.ReadIntPtr(children, (int)(count - 1) * IntPtr.Size) : 0;
        }
        finally
        {
            if (children != 0) XFree(children);
        }
    }

    // Called with _lock held.
    private bool IsViewable(nint window) =>
        XGetWindowAttributes(_display, window, out XWindowAttributes attrs) != 0 && attrs.MapState == IsViewableState;

    public void Dispose()
    {
        if (_disposed) return;
        _stopping = true;
        _thread.Join(TimeSpan.FromSeconds(2));
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            XUngrabButton(_display, AnyButton, AnyModifier, _parent);
            XCloseDisplay(_display);
        }
    }

    // ── Xlib interop ───────────────────────────────────────────────────────

    private const string LibX11 = "libX11.so.6";

    private const string LibXss = "libXss.so.1";

    private const int KeyPress = 2;
    private const int KeyRelease = 3;
    private const int ButtonPress = 4;
    private const int MapNotify = 19;
    private const int ConfigureNotify = 22;

    private const nint KeyPressMask = 1 << 0;
    private const nint KeyReleaseMask = 1 << 1;
    private const nint ButtonPressMask = 1 << 2;
    private const nint StructureNotifyMask = 1 << 17;
    private const nint SubstructureNotifyMask = 1 << 19;

    private const uint AnyButton = 0;
    private const uint AnyModifier = 1 << 15;
    private const int GrabModeSync = 0;
    private const int GrabModeAsync = 1;
    private const int ReplayPointer = 2;
    private const int RevertToParent = 2;
    private const nint CurrentTime = 0;
    private const int IsViewableState = 2;
    private const uint ControlMask = 1 << 2;
    private const uint Mod1Mask = 1 << 3;
    private const short PollIn = 1;

    /// <summary>
    /// Xlib's XEvent union (24 longs). Only the fields we read are declared; offsets are for LP64.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XEvent
    {
        [FieldOffset(0)] public int Type;

        // XButtonEvent: type, serial, send_event, display, window, root, subwindow, time, ...
        [FieldOffset(56)] public nint ButtonTime;

        // XMapEvent: type, serial, send_event, display, event, window, override_redirect
        [FieldOffset(32)] public nint MapEvent;
        [FieldOffset(40)] public nint MapWindow;

        // XConfigureEvent: ..., event (32), window (40), x (48), y (52), width (56), height (60), ...
        [FieldOffset(40)] public nint ConfigureWindow;
        [FieldOffset(56)] public int ConfigureWidth;
        [FieldOffset(60)] public int ConfigureHeight;
    }

    /// <summary>XKeyEvent inside the 192-byte XEvent union (LP64 offsets).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 192)]
    private struct XKeyEvent
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(16)] public int SendEvent;
        [FieldOffset(32)] public nint Window;
        [FieldOffset(40)] public nint Root;
        [FieldOffset(56)] public nint Time;
        [FieldOffset(80)] public uint State;
        [FieldOffset(84)] public uint Keycode;
        [FieldOffset(88)] public int SameScreen;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XScreenSaverInfo
    {
        public nint Window;
        public int State;
        public int Kind;
        public nuint TilOrSince;
        public nuint Idle;
        public nuint EventMask;
    }

    [StructLayout(LayoutKind.Explicit, Size = 136)]
    private struct XWindowAttributes
    {
        [FieldOffset(8)] public int Width;
        [FieldOffset(12)] public int Height;
        [FieldOffset(92)] public int MapState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport(LibX11)] private static extern nint XOpenDisplay(nint displayName);
    [DllImport(LibX11)] private static extern int XCloseDisplay(nint display);
    [DllImport(LibX11)] private static extern int XConnectionNumber(nint display);
    [DllImport(LibX11)] private static extern int XPending(nint display);
    [DllImport(LibX11)] private static extern int XNextEvent(nint display, out XEvent ev);
    [DllImport(LibX11)] private static extern int XFlush(nint display);
    [DllImport(LibX11)] private static extern int XFree(nint data);
    [DllImport(LibX11)] private static extern int XSelectInput(nint display, nint window, nint eventMask);
    [DllImport(LibX11)] private static extern int XQueryTree(nint display, nint window, out nint root, out nint parent, out nint children, out uint count);
    [DllImport(LibX11)] private static extern int XResizeWindow(nint display, nint window, uint width, uint height);
    [DllImport(LibX11)] private static extern int XSetInputFocus(nint display, nint focus, int revertTo, nint time);
    [DllImport(LibX11)] private static extern int XGetInputFocus(nint display, out nint focus, out int revertTo);
    [DllImport(LibX11)] private static extern int XGetWindowAttributes(nint display, nint window, out XWindowAttributes attributes);
    [DllImport(LibX11)] private static extern int XAllowEvents(nint display, int eventMode, nint time);
    [DllImport(LibX11)] private static extern nint XDefaultRootWindow(nint display);

    [DllImport(LibX11)]
    private static extern int XClearArea(nint display, nint window, int x, int y, uint width, uint height,
        [MarshalAs(UnmanagedType.Bool)] bool exposures);
    [DllImport(LibX11)] private static extern byte XKeysymToKeycode(nint display, nint keysym); // KeyCode is an unsigned char

    [DllImport(LibX11)]
    private static extern int XSendEvent(nint display, nint window, [MarshalAs(UnmanagedType.Bool)] bool propagate,
        nint eventMask, ref XKeyEvent ev);

    [DllImport(LibX11)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool XQueryPointer(nint display, nint window, out nint root, out nint child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [DllImport(LibXss)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool XScreenSaverQueryExtension(nint display, out int eventBase, out int errorBase);

    [DllImport(LibXss)] private static extern int XScreenSaverQueryInfo(nint display, nint drawable, out XScreenSaverInfo info);

    [DllImport(LibX11)]
    private static extern int XGrabButton(nint display, uint button, uint modifiers, nint grabWindow,
        [MarshalAs(UnmanagedType.Bool)] bool ownerEvents, uint eventMask, int pointerMode, int keyboardMode,
        nint confineTo, nint cursor);

    [DllImport(LibX11)] private static extern int XUngrabButton(nint display, uint button, uint modifiers, nint grabWindow);

    [DllImport("libc", SetLastError = true)] private static extern int poll(ref PollFd fds, nuint nfds, int timeout);
}
