using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace mRemoteNG.Protocols.Rdp;

/// <summary>
/// Native child window that FreeRDP is parented to (<c>/parent-window</c>).
///
/// Avalonia destroys a <see cref="NativeControlHost"/>'s native window when the control leaves the visual
/// tree (e.g. when another session tab is selected) and creates a new one when it comes back. Destroying
/// the window would destroy FreeRDP's child window with it and kill the session, so this host keeps the
/// first native window alive across detach/attach cycles (Avalonia parks it under an off-screen window
/// while detached) and hands the same handle back on re-attach. The XID/HWND FreeRDP was given therefore
/// stays valid for the whole session; it is destroyed only by <see cref="Release"/>.
/// </summary>
internal sealed class RdpNativeHost : NativeControlHost
{
    private readonly TaskCompletionSource<IPlatformHandle> _handleReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IPlatformHandle? _handle;
    private bool _heldByAvalonia;
    private bool _released;

    public RdpNativeHost()
    {
        Focusable = true;
    }

    /// <summary>Completes with the native parent handle once Avalonia has created it.</summary>
    public Task<IPlatformHandle> HandleReady => _handleReady.Task;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (_handle is null)
        {
            _handle = base.CreateNativeControlCore(parent);
            _handleReady.TrySetResult(_handle);
        }
        _heldByAvalonia = true;
        return _handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _heldByAvalonia = false;
        if (_released)
            DestroyRetainedHandle();
    }

    /// <summary>Destroys the native window now, or as soon as Avalonia lets go of it. UI thread only.</summary>
    public void Release()
    {
        _released = true;
        if (!_heldByAvalonia)
            DestroyRetainedHandle();
    }

    private void DestroyRetainedHandle()
    {
        if (_handle is INativeControlHostDestroyableControlHandle destroyable)
            destroyable.Destroy();
        _handle = null;
    }
}

/// <summary>
/// Content of an RDP session tab: the native host FreeRDP draws into, plus a message panel used before
/// the session starts, after it ends, and when FreeRDP runs in its own window (macOS, non-X11 Linux).
/// Native windows always paint above Avalonia content, so the two are never shown at the same time.
/// </summary>
internal sealed class RdpSessionView : Grid
{
    // Views shown in a window, for the "another window opened" notification (OnAnyWindowOpened). UI thread only.
    private static readonly List<WeakReference<RdpSessionView>> ShownViews = [];

    // Windows opened over this view (dialogs, prompts) that are still open.
    private readonly HashSet<Window> _windowsOver = [];
    private readonly Border _messagePanel;
    private readonly TextBlock _messageText;
    private readonly Button _bringToFrontButton;
    private readonly Button _disconnectButton;
    private readonly TaskCompletionSource _sized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TopLevel? _topLevel;

    static RdpSessionView()
    {
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => OnAnyWindowOpened(window), handledEventsToo: true);
        Window.WindowClosedEvent.AddClassHandler<Window>((window, _) => OnAnyWindowClosed(window), handledEventsToo: true);
    }

    /// <param name="embeddingCandidate">False on platforms where FreeRDP can never draw into the tab (macOS).</param>
    /// <param name="iconClass">
    /// Style class of the message panel's icon ("rdp", "app"); the application's styles put the matching glyph into
    /// <c>ContentControl.session-message-icon</c>.
    /// </param>
    public RdpSessionView(bool embeddingCandidate, string iconClass = "rdp")
    {
        Background = Brushes.Black;
        Focusable = true;

        if (embeddingCandidate)
        {
            Host = new RdpNativeHost();
            Children.Add(Host);
        }

        // Colours, fonts and the icon come from the application's styles (classes "session-message…") so the panel
        // follows the theme; without them it shows the window's text colour on the view's black background.
        _messageText = new TextBlock
        {
            Classes = { "session-message-text" },
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 560,
        };
        _bringToFrontButton = new Button { Content = "Bring window to front", IsVisible = false };
        _disconnectButton = new Button { Content = "Disconnect", IsVisible = false };
        _bringToFrontButton.Click += (_, _) => BringToFrontRequested?.Invoke(this, EventArgs.Empty);
        _disconnectButton.Click += (_, _) => DisconnectRequested?.Invoke(this, EventArgs.Empty);

        _messagePanel = new Border
        {
            Classes = { "session-message" },
            Child = new StackPanel
            {
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new ContentControl
                    {
                        Classes = { "session-message-icon", iconClass },
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Focusable = false,
                    },
                    _messageText,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 4, 0, 0),
                        Children = { _bringToFrontButton, _disconnectButton },
                    },
                },
            },
        };
        Children.Add(_messagePanel);
        ShowMessage("Preparing RDP session…");
    }

    /// <summary>The native parent for FreeRDP, or null when this platform cannot embed (macOS).</summary>
    public RdpNativeHost? Host { get; }

    public event EventHandler? BringToFrontRequested;
    public event EventHandler? DisconnectRequested;

    /// <summary>Raised when the view becomes visible in a tab (attached to a window).</summary>
    public event EventHandler? Shown;

    /// <summary>
    /// Raised when the user presses a pointer button on Avalonia UI of this window other than this session's
    /// tab header (clicks on the remote desktop itself go to the native window and never reach Avalonia).
    /// </summary>
    public event EventHandler? AvaloniaPointerPressed;

    /// <summary>Raised (after Avalonia processed the click) when the tab header showing this view is pressed.</summary>
    public event EventHandler? TabHeaderPressed;

    /// <summary>Raised when the window hosting this view is activated by the window manager.</summary>
    public event EventHandler? WindowActivated;

    /// <summary>
    /// Raised when another Avalonia window (a dialog, a prompt) opens while this view is shown: the keyboard has to
    /// leave the native window so typing reaches the new window instead of the remote desktop.
    /// </summary>
    public event EventHandler? KeyboardReleaseRequested;

    /// <summary>Raised when the last window that caused <see cref="KeyboardReleaseRequested"/> has closed.</summary>
    public event EventHandler? KeyboardReleaseEnded;

    /// <summary>True while a window opened over this view (see <see cref="KeyboardReleaseRequested"/>) is still open.</summary>
    public bool IsCoveredByWindow => _windowsOver.Count > 0;

    /// <summary>Raised with the new size in device pixels.</summary>
    public event EventHandler<PixelSize>? PixelSizeChanged;

    /// <summary>Completes once the view has a non-empty size.</summary>
    public Task Sized => _sized.Task;

    /// <summary>The current size in device pixels (UI thread).</summary>
    public PixelSize PixelSize
    {
        get
        {
            double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            return new PixelSize(
                Math.Max(1, (int)Math.Round(Bounds.Width * scale)),
                Math.Max(1, (int)Math.Round(Bounds.Height * scale)));
        }
    }

    /// <summary>True while the view is attached to a window (its tab is the selected one).</summary>
    public bool IsShownInWindow => _topLevel is not null;

    /// <summary>The native handle of the window hosting this view (UI thread), or 0.</summary>
    public nint TopLevelHandle => TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? 0;

    /// <summary>Shows the native host (FreeRDP's window) and hides the message panel.</summary>
    public void ShowEmbedded()
    {
        if (Host is not null) Host.IsVisible = true;
        _messagePanel.IsVisible = false;
    }

    /// <summary>Shows a message instead of the remote desktop.</summary>
    public void ShowMessage(string message, bool showBringToFront = false, bool showDisconnect = false)
    {
        _messageText.Text = message;
        _bringToFrontButton.IsVisible = showBringToFront;
        _disconnectButton.IsVisible = showDisconnect;
        _messagePanel.IsVisible = true;
        // The native window would paint over the panel; hiding the host keeps its native window alive.
        if (Host is not null) Host.IsVisible = false;
    }

    /// <summary>Text currently shown by the message panel, or null when the remote desktop is shown.</summary>
    public string? VisibleMessage => _messagePanel.IsVisible ? _messageText.Text : null;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = e.Root as TopLevel;
        ShownViews.RemoveAll(r => !r.TryGetTarget(out var view) || ReferenceEquals(view, this));
        ShownViews.Add(new WeakReference<RdpSessionView>(this));
        _topLevel?.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (_topLevel is WindowBase window)
            window.Activated += OnWindowActivated;
        // Let Avalonia map the native window first (it does so after layout/render).
        Dispatcher.UIThread.Post(() => Shown?.Invoke(this, EventArgs.Empty), DispatcherPriority.Background);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
        if (_topLevel is WindowBase window)
            window.Activated -= OnWindowActivated;
        _topLevel = null;
        ShownViews.RemoveAll(r => !r.TryGetTarget(out var view) || ReferenceEquals(view, this));
        _windowsOver.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    private static List<RdpSessionView> LiveShownViews()
    {
        ShownViews.RemoveAll(r => !r.TryGetTarget(out _));
        return ShownViews.Select(r => r.TryGetTarget(out var view) ? view : null).OfType<RdpSessionView>().ToList();
    }

    private static void OnAnyWindowOpened(Window window)
    {
        var released = false;
        foreach (var view in LiveShownViews())
        {
            // The view's own window (e.g. the full-screen window it has just moved into) is not "another" window.
            if (view._topLevel is null || ReferenceEquals(view._topLevel, window) || !view._windowsOver.Add(window))
                continue;
            view.KeyboardReleaseRequested?.Invoke(view, EventArgs.Empty);
            released = true;
        }

        // The keyboard is back in our application: make sure the new window, not the one under it, gets it.
        if (released)
            Dispatcher.UIThread.Post(() =>
            {
                if (window.IsVisible)
                    window.Activate();
            });
    }

    private static void OnAnyWindowClosed(Window window)
    {
        foreach (var view in LiveShownViews())
        {
            if (view._windowsOver.Remove(window) && view._windowsOver.Count == 0)
                view.KeyboardReleaseEnded?.Invoke(view, EventArgs.Empty);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            _sized.TrySetResult();
            PixelSizeChanged?.Invoke(this, PixelSize);
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e) => WindowActivated?.Invoke(this, EventArgs.Empty);

    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only a left click on the tab means "work in the remote desktop". A right click opens the tab's context menu
        // and a middle click closes the tab: handing the keyboard to the native window then deactivated our window,
        // which closed the context menu as soon as it opened (it took several right clicks to get the menu).
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && IsOwnTabHeader(e.Source as Visual))
        {
            // Run after the click was processed (and the window manager focused our window) so the focus
            // handed to the remote desktop is not immediately taken back.
            Dispatcher.UIThread.Post(() => TabHeaderPressed?.Invoke(this, EventArgs.Empty), DispatcherPriority.Background);
        }
        else
        {
            AvaloniaPointerPressed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// True when <paramref name="source"/> lies in an element bound to a session object whose ContentView is
    /// this view, i.e. the tab header of this session. Matches the property by name so the protocol library
    /// does not depend on the application's view models.
    /// </summary>
    private bool IsOwnTabHeader(Visual? source)
    {
        for (var visual = source; visual is not null && !ReferenceEquals(visual, this); visual = visual.GetVisualParent())
        {
            if (visual is StyledElement { DataContext: { } context }
                && context.GetType().GetProperty("ContentView")?.GetValue(context) is { } content
                && ReferenceEquals(content, this))
                return true;
        }
        return false;
    }
}
