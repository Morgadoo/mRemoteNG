using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace mRemoteNG.Protocols.Embedding;

/// <summary>
/// Finds the top-level X11 window of a launched program (by <c>_NET_WM_PID</c>, else the owning client's pid from the
/// X-Resource extension, else a new window whose WM_CLASS is the program's name) and moves it into one of our
/// native windows with XReparentWindow. Uses its own short-lived Xlib connection per operation; window ids are
/// server-side, so they are valid on any connection. X errors (e.g. a window vanishing between two calls) go to the
/// process-wide handler installed by Avalonia's X11 backend, which only records them — so this class must only be
/// used when that backend is active (the native parent handle is an "XID").
/// </summary>
[SupportedOSPlatform("linux")]
internal static class X11ForeignWindows
{
    private const int MaxTreeDepth = 4;

    /// <summary>
    /// Polls until a window of <paramref name="pid"/> (or one of its child processes) is shown, the process exits or
    /// <paramref name="timeout"/> passes. Returns the window, or 0.
    /// </summary>
    public static async Task<nint> WaitForProcessWindowAsync(int pid, Func<bool> hasExited, TimeSpan timeout, ILogger logger, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        // Windows already shown when the program was started can never be its window (WM_CLASS fallback).
        var preexisting = Scan(logger)?.Select(c => c.Window).ToHashSet() ?? [];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var pids = ForeignWindowDiscovery.GetProcessTree(pid, ForeignWindowDiscovery.ReadLinuxProcessTable());
            if (Scan(logger) is { } candidates)
            {
                nint window = ForeignWindowDiscovery.SelectWindow(candidates, pids, pid);
                if (window == 0)
                    window = ForeignWindowDiscovery.SelectWindowByClass(candidates, preexisting, ForeignWindowDiscovery.ReadLinuxProcessNames(pids));
                if (window != 0)
                    return window;
            }
            if (hasExited() || DateTime.UtcNow >= deadline)
                return 0;
            await Task.Delay(100, ct);
        }
    }

    /// <summary>One scan of the window tree; null when the display cannot be opened.</summary>
    private static List<WindowCandidate>? Scan(ILogger logger)
    {
        nint display = XOpenDisplay(0);
        if (display == 0)
        {
            logger.LogWarning("Could not open the X display to look for the program's window");
            return null;
        }
        try
        {
            nint root = XDefaultRootWindow(display);
            nint pidAtom = XInternAtom(display, "_NET_WM_PID", false);
            nint wmStateAtom = XInternAtom(display, "WM_STATE", false);
            var candidates = new List<WindowCandidate>();
            Collect(display, root, root, 0, pidAtom, wmStateAtom, candidates);
            return candidates;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    private static void Collect(nint display, nint root, nint window, int depth, nint pidAtom, nint wmStateAtom, List<WindowCandidate> candidates)
    {
        if (depth >= MaxTreeDepth)
            return;
        foreach (nint child in QueryChildren(display, window))
        {
            if (XGetWindowAttributes(display, child, out XWindowAttributes attrs) == 0)
                continue;
            int? pid = ReadCardinal(display, child, pidAtom) is { } value ? (int)value : null;
            bool hasWmState = HasProperty(display, child, wmStateAtom);
            string? instance = null, className = null;
            if (pid is null && (hasWmState || window == root))
            {
                pid = XResClientPid(display, child);
                (instance, className) = ReadClassHint(display, child);
            }
            candidates.Add(new WindowCandidate(child, pid, hasWmState, attrs.MapState == IsViewable,
                attrs.OverrideRedirect != 0, window == root, attrs.Width, attrs.Height, instance, className));
            // A managed client window is a leaf for our purposes; frames and containers are searched further.
            if (!hasWmState)
                Collect(display, root, child, depth + 1, pidAtom, wmStateAtom, candidates);
        }
    }

    /// <summary>
    /// Takes <paramref name="window"/> away from the window manager and makes it a child of <paramref name="parent"/>,
    /// filling it (<paramref name="width"/> × <paramref name="height"/> device pixels).
    /// </summary>
    public static async Task<bool> ReparentAsync(nint window, nint parent, int width, int height, ILogger logger, CancellationToken ct)
    {
        nint display = XOpenDisplay(0);
        if (display == 0)
            return false;
        try
        {
            nint root = XDefaultRootWindow(display);

            // The host must be shown at its real size first: the embed glue sizes the program's window to the
            // parent's size when it is mapped, and a program (e.g. a terminal) lays itself out for that size.
            var shownDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < shownDeadline
                   && !(XGetWindowAttributes(display, parent, out XWindowAttributes parentAttrs) != 0
                        && parentAttrs.MapState == IsViewable && parentAttrs.Width > 1 && parentAttrs.Height > 1))
                await Task.Delay(20, ct);

            if (GetParent(display, window) is var current && current != root && current != parent && current != 0)
            {
                // Managed by a window manager (inside its frame): withdraw it (ICCCM 4.1.4) so the window manager
                // unmanages it and gives it back to the root, then take it from there.
                XWithdrawWindow(display, window, XDefaultScreen(display));
                XSync(display, false);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
                while (GetParent(display, window) is var p && p != root && p != 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(20, ct);
            }

            XReparentWindow(display, window, parent, 0, 0);
            XMoveResizeWindow(display, window, 0, 0, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
            XMapWindow(display, window);
            XSync(display, false);
            bool done = GetParent(display, window) == parent;
            if (!done)
                logger.LogWarning("Could not move window 0x{Window:x} into the session tab", (long)window);
            return done;
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    /// <summary>The window's parent, or 0 when it no longer exists.</summary>
    private static nint GetParent(nint display, nint window)
    {
        if (XQueryTree(display, window, out _, out nint parent, out nint children, out _) == 0)
            return 0;
        if (children != 0)
            XFree(children);
        return parent;
    }

    private static List<nint> QueryChildren(nint display, nint window)
    {
        var result = new List<nint>();
        if (XQueryTree(display, window, out _, out _, out nint children, out uint count) == 0)
            return result;
        try
        {
            for (int i = 0; i < count; i++)
                result.Add(Marshal.ReadIntPtr(children, i * IntPtr.Size));
        }
        finally
        {
            if (children != 0)
                XFree(children);
        }
        return result;
    }

    private static long? ReadCardinal(nint display, nint window, nint property)
    {
        if (XGetWindowProperty(display, window, property, 0, 1, false, XaCardinal,
                out _, out int format, out nuint items, out _, out nint data) != 0)
            return null;
        try
        {
            // Format-32 properties are returned as an array of C longs.
            return format == 32 && items > 0 && data != 0 ? Marshal.ReadIntPtr(data).ToInt64() : null;
        }
        finally
        {
            if (data != 0)
                XFree(data);
        }
    }

    private static bool HasProperty(nint display, nint window, nint property)
    {
        if (XGetWindowProperty(display, window, property, 0, 0, false, AnyPropertyType,
                out nint actualType, out _, out _, out _, out nint data) != 0)
            return false;
        if (data != 0)
            XFree(data);
        return actualType != 0;
    }

    private static (string? Instance, string? Class) ReadClassHint(nint display, nint window)
    {
        if (XGetClassHint(display, window, out XClassHint hint) == 0)
            return (null, null);
        try
        {
            return (Marshal.PtrToStringAnsi(hint.ResName), Marshal.PtrToStringAnsi(hint.ResClass));
        }
        finally
        {
            if (hint.ResName != 0)
                XFree(hint.ResName);
            if (hint.ResClass != 0)
                XFree(hint.ResClass);
        }
    }

    private static bool _xresUnavailable;

    /// <summary>
    /// The pid of the X client that created <paramref name="window"/>, as known to the X server (X-Resource extension
    /// 1.2, local clients only). Null when unknown or when libXRes is not installed.
    /// </summary>
    private static int? XResClientPid(nint display, nint window)
    {
        if (_xresUnavailable)
            return null;
        try
        {
            var spec = new XResClientIdSpec { Client = (nuint)window, Mask = XResClientIdPidMask };
            if (XResQueryClientIds(display, 1, ref spec, out nint count, out nint values) != 0 || values == 0)
                return null;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    int pid = XResGetClientPid(values + i * Marshal.SizeOf<XResClientIdValue>());
                    if (pid > 0)
                        return pid;
                }
                return null;
            }
            finally
            {
                XResClientIdsDestroy(count, values);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _xresUnavailable = true;
            return null;
        }
    }

    // ── Xlib interop ───────────────────────────────────────────────────────

    private const string LibX11 = "libX11.so.6";
    private const int IsViewable = 2;
    private const nint XaCardinal = 6;
    private const nint AnyPropertyType = 0;

    /// <summary>XWindowAttributes (LP64 layout); only the fields we read are declared.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 136)]
    private struct XWindowAttributes
    {
        [FieldOffset(8)] public int Width;
        [FieldOffset(12)] public int Height;
        [FieldOffset(92)] public int MapState;
        [FieldOffset(120)] public int OverrideRedirect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XClassHint
    {
        public nint ResName;
        public nint ResClass;
    }

    private const string LibXRes = "libXRes.so.1";
    private const uint XResClientIdPidMask = 1 << 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct XResClientIdSpec
    {
        public nuint Client;
        public uint Mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XResClientIdValue
    {
        public XResClientIdSpec Spec;
        public nint Length;
        public nint Value;
    }

    [DllImport(LibXRes)] private static extern int XResQueryClientIds(nint display, nint specCount, ref XResClientIdSpec specs, out nint idCount, out nint ids);
    [DllImport(LibXRes)] private static extern int XResGetClientPid(nint value);
    [DllImport(LibXRes)] private static extern void XResClientIdsDestroy(nint idCount, nint ids);
    [DllImport(LibX11)] private static extern int XGetClassHint(nint display, nint window, out XClassHint hint);
    [DllImport(LibX11)] private static extern nint XOpenDisplay(nint displayName);
    [DllImport(LibX11)] private static extern int XCloseDisplay(nint display);
    [DllImport(LibX11)] private static extern nint XDefaultRootWindow(nint display);
    [DllImport(LibX11)] private static extern int XDefaultScreen(nint display);
    [DllImport(LibX11)] private static extern int XSync(nint display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [DllImport(LibX11)] private static extern int XFree(nint data);
    [DllImport(LibX11)] private static extern nint XInternAtom(nint display, string atomName, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
    [DllImport(LibX11)] private static extern int XQueryTree(nint display, nint window, out nint root, out nint parent, out nint children, out uint count);
    [DllImport(LibX11)] private static extern int XGetWindowAttributes(nint display, nint window, out XWindowAttributes attributes);
    [DllImport(LibX11)] private static extern int XReparentWindow(nint display, nint window, nint parent, int x, int y);
    [DllImport(LibX11)] private static extern int XMoveResizeWindow(nint display, nint window, int x, int y, uint width, uint height);
    [DllImport(LibX11)] private static extern int XMapWindow(nint display, nint window);
    [DllImport(LibX11)] private static extern int XWithdrawWindow(nint display, nint window, int screen);

    [DllImport(LibX11)]
    private static extern int XGetWindowProperty(nint display, nint window, nint property, nint longOffset, nint longLength,
        [MarshalAs(UnmanagedType.Bool)] bool delete, nint reqType, out nint actualType, out int actualFormat,
        out nuint itemCount, out nuint bytesAfter, out nint data);
}
