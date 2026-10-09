using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using mRemoteNG.Protocols.Vnc.Rfb;

namespace mRemoteNG.Protocols.Vnc;

/// <summary>
/// Session surface for <see cref="VncProtocol"/>: shows the remote framebuffer and forwards keyboard, mouse and
/// clipboard input. Framebuffer changes arrive on the receive thread and are coalesced into at most one pending
/// UI-thread copy of the accumulated dirty rectangle.
/// </summary>
internal sealed class VncView : UserControl
{
    private readonly VncProtocol _protocol;
    private readonly ScrollViewer _scroller;
    private readonly FramebufferSurface _surface;
    private readonly TextBlock _status;
    private readonly Dictionary<Key, uint> _pressedKeys = new();
    private byte[] _rowBuffer = [];
    private int _renderQueued;
    private RfbButtons _buttons;
    private Vector _wheelRemainder;
    private string? _clipboardText;

    public VncView(VncProtocol protocol)
    {
        _protocol = protocol;
        Focusable = true;
        Background = Brushes.Black;
        ClipToBounds = true;

        _surface = new FramebufferSurface();
        _scroller = new ScrollViewer
        {
            Content = _surface,
            Focusable = false,
        };
        _status = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false,
        };

        var root = new Grid();
        root.Children.Add(_scroller);
        root.Children.Add(_status);
        Content = root;

        // The wheel must reach the remote desktop before the ScrollViewer consumes it.
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        ApplySettings();
    }

    // ── Calls from VncProtocol (any thread) ────────────────────────────────

    public void ApplySettings() => OnUiThread(() =>
    {
        var scaling = _protocol.Scaling;
        _surface.Scaling = scaling;
        var bars = scaling == VncScaling.None ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroller.HorizontalScrollBarVisibility = bars;
        _scroller.VerticalScrollBarVisibility = bars;
    });

    /// <summary>Called before view-only mode changes: release held input, or take focus to start sending it.</summary>
    public void BeforeViewOnlyChange(bool viewOnly) => OnUiThread(() =>
    {
        if (viewOnly)
            ReleaseAll();
        else if (IsAttachedToVisualTree())
            Focus();
    });

    public void ShowStatus(string text) => OnUiThread(() =>
    {
        _status.Text = text;
        _status.IsVisible = true;
    });

    public void OnConnected() => OnUiThread(() =>
    {
        _status.IsVisible = false;
        if (!_protocol.ViewOnly && IsAttachedToVisualTree())
            Focus();
    });

    public void OnFramebufferUpdated()
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
            Dispatcher.UIThread.Post(RenderPending, DispatcherPriority.Render);
    }

    public void OnCursorChanged(RfbCursor cursor) => OnUiThread(() =>
    {
        if (cursor.Width == 0 || cursor.Height == 0)
        {
            var hidden = _surface.Cursor;
            _surface.Cursor = new Cursor(StandardCursorType.None);
            hidden?.Dispose();
            return;
        }

        var bitmap = new WriteableBitmap(new PixelSize(cursor.Width, cursor.Height), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var locked = bitmap.Lock())
            CopyPixels(cursor.Bgra, cursor.Width, new RfbRect(0, 0, cursor.Width, cursor.Height), locked);
        var previous = _surface.Cursor;
        _surface.Cursor = new Cursor(bitmap, new PixelPoint(cursor.HotspotX, cursor.HotspotY));
        previous?.Dispose();
    });

    public void OnServerClipboard(string text) => OnUiThread(() =>
    {
        _clipboardText = text;
        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    });

    // ── Rendering ──────────────────────────────────────────────────────────

    private void RenderPending()
    {
        Volatile.Write(ref _renderQueued, 0);
        var framebuffer = _protocol.Framebuffer;
        if (framebuffer is null) return;

        // Take the dirty region before the snapshot: a resize in between re-marks everything dirty.
        var dirty = framebuffer.TakeDirty();
        var snapshot = framebuffer.Snapshot;
        if (snapshot.Width <= 0 || snapshot.Height <= 0) return;

        var bitmap = _surface.Bitmap;
        if (bitmap is null || bitmap.PixelSize.Width != snapshot.Width || bitmap.PixelSize.Height != snapshot.Height)
        {
            bitmap = new WriteableBitmap(new PixelSize(snapshot.Width, snapshot.Height), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, AlphaFormat.Opaque);
            dirty = new RfbRect(0, 0, snapshot.Width, snapshot.Height);
            _surface.Bitmap = bitmap;
        }

        dirty = Intersect(dirty, snapshot.Width, snapshot.Height);
        if (dirty.IsEmpty) return;

        using (var locked = bitmap.Lock())
            CopyPixels(snapshot.Pixels, snapshot.Width, dirty, locked);
        _surface.InvalidateVisual();
    }

    private void CopyPixels(uint[] source, int sourceWidth, RfbRect rect, ILockedFramebuffer target)
    {
        var rowBytes = rect.Width * 4;
        if (_rowBuffer.Length < rowBytes) _rowBuffer = new byte[rowBytes];
        for (var y = rect.Y; y < rect.Bottom; y++)
        {
            MemoryMarshal.AsBytes(source.AsSpan(y * sourceWidth + rect.X, rect.Width)).CopyTo(_rowBuffer);
            Marshal.Copy(_rowBuffer, 0, target.Address + y * target.RowBytes + rect.X * 4, rowBytes);
        }
    }

    private static RfbRect Intersect(RfbRect r, int width, int height)
    {
        var x = Math.Max(r.X, 0);
        var y = Math.Max(r.Y, 0);
        var right = Math.Min(r.Right, width);
        var bottom = Math.Min(r.Bottom, height);
        return right <= x || bottom <= y ? default : new RfbRect(x, y, right - x, bottom - y);
    }

    // ── Keyboard ───────────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_protocol.AcceptsInput)
        {
            base.OnKeyDown(e);
            return;
        }

        e.Handled = true;
        // Auto-repeat re-sends the press with the keysym chosen on the first press.
        if (!_pressedKeys.TryGetValue(e.Key, out var keysym))
        {
            var mapped = X11KeySymbols.FromKey(e.Key, e.KeySymbol, e.KeyModifiers);
            if (mapped is null) return;
            keysym = mapped.Value;
            _pressedKeys[e.Key] = keysym;
        }
        _protocol.SendKey(keysym, true);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (!_protocol.AcceptsInput)
        {
            base.OnKeyUp(e);
            return;
        }

        e.Handled = true;
        // Release exactly what was pressed, even if modifiers changed the symbol in between.
        if (_pressedKeys.Remove(e.Key, out var keysym))
            _protocol.SendKey(keysym, false);
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        _ = PushClipboardAsync();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        ReleaseAll();
    }

    /// <summary>Sends local clipboard text to the server when it changed since the last exchange.</summary>
    private async Task PushClipboardAsync()
    {
        if (!_protocol.AcceptsInput) return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        try
        {
            var text = await clipboard.GetTextAsync();
            if (!string.IsNullOrEmpty(text) && text != _clipboardText)
            {
                _clipboardText = text;
                _protocol.SendClipboardText(text);
            }
        }
        catch (Exception)
        {
            // Clipboard access is best effort (it can fail while another application owns it).
        }
    }

    /// <summary>Releases keys and buttons the server still believes are held, so nothing sticks.</summary>
    private void ReleaseAll()
    {
        foreach (var keysym in _pressedKeys.Values)
            _protocol.ReleaseKey(keysym);
        _pressedKeys.Clear();
        if (_buttons != RfbButtons.None)
        {
            _buttons = RfbButtons.None;
            var (x, y) = _surface.LastPosition;
            _protocol.ReleaseButtons(x, y);
        }
    }

    // ── Mouse ──────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!ReferenceEquals(e.Source, _surface)) return;
        Focus();
        SendPointer(e);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        SendPointer(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (ReferenceEquals(e.Source, _surface) || _buttons != RfbButtons.None)
            SendPointer(e);
    }

    private void SendPointer(PointerEventArgs e)
    {
        if (!_protocol.AcceptsInput) return;
        var point = e.GetCurrentPoint(_surface);
        var props = point.Properties;
        _buttons = (props.IsLeftButtonPressed ? RfbButtons.Left : 0)
            | (props.IsMiddleButtonPressed ? RfbButtons.Middle : 0)
            | (props.IsRightButtonPressed ? RfbButtons.Right : 0);
        var (x, y) = _surface.MapToFramebuffer(point.Position);
        _protocol.SendPointer(x, y, _buttons);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!ReferenceEquals(e.Source, _surface) || !_protocol.AcceptsInput) return;
        e.Handled = true;

        var (x, y) = _surface.MapToFramebuffer(e.GetPosition(_surface));
        // Accumulate fractional (touchpad) deltas; each whole notch is one press + release of buttons 4-7.
        _wheelRemainder += e.Delta;
        while (Math.Abs(_wheelRemainder.Y) >= 1)
        {
            var up = _wheelRemainder.Y > 0;
            Click(x, y, up ? RfbButtons.WheelUp : RfbButtons.WheelDown);
            _wheelRemainder = _wheelRemainder.WithY(_wheelRemainder.Y + (up ? -1 : 1));
        }
        while (Math.Abs(_wheelRemainder.X) >= 1)
        {
            var left = _wheelRemainder.X > 0;
            Click(x, y, left ? RfbButtons.WheelLeft : RfbButtons.WheelRight);
            _wheelRemainder = _wheelRemainder.WithX(_wheelRemainder.X + (left ? -1 : 1));
        }
    }

    private void Click(int x, int y, RfbButtons wheelButton)
    {
        _protocol.SendPointer(x, y, _buttons | wheelButton);
        _protocol.SendPointer(x, y, _buttons);
    }

    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    private bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;
}

/// <summary>Draws the framebuffer bitmap according to the scaling mode and maps pointer positions back.</summary>
internal sealed class FramebufferSurface : Control
{
    private WriteableBitmap? _bitmap;
    private VncScaling _scaling = VncScaling.Fit;

    public WriteableBitmap? Bitmap
    {
        get => _bitmap;
        set
        {
            if (ReferenceEquals(_bitmap, value)) return;
            // The previous bitmap may still be referenced by the last rendered frame; let the GC reclaim it.
            _bitmap = value;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public VncScaling Scaling
    {
        get => _scaling;
        set
        {
            _scaling = value;
            RenderOptions.SetBitmapInterpolationMode(this,
                value == VncScaling.None ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>Framebuffer coordinates of the last mapped pointer position.</summary>
    public (int X, int Y) LastPosition { get; private set; }

    /// <summary>Size of one framebuffer pixel in device-independent units at 1:1.</summary>
    private double PixelDip => 1.0 / (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_bitmap is null) return default;
        var native = new Size(_bitmap.PixelSize.Width * PixelDip, _bitmap.PixelSize.Height * PixelDip);
        if (_scaling == VncScaling.None) return native;
        return new Size(
            double.IsInfinity(availableSize.Width) ? native.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? native.Height : availableSize.Height);
    }

    public override void Render(DrawingContext context)
    {
        // A transparent fill makes the whole surface (including letterbox bars) hit-testable.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_bitmap is null) return;
        var size = _bitmap.PixelSize;
        context.DrawImage(_bitmap, new Rect(0, 0, size.Width, size.Height), DestinationRect());
    }

    /// <summary>Where the framebuffer is drawn within this control.</summary>
    public Rect DestinationRect()
    {
        if (_bitmap is null) return default;
        double w = _bitmap.PixelSize.Width, h = _bitmap.PixelSize.Height;
        var bounds = Bounds.Size;
        switch (_scaling)
        {
            case VncScaling.Stretch:
                return new Rect(bounds);
            case VncScaling.Fit:
                var scale = Math.Min(bounds.Width / w, bounds.Height / h);
                if (!(scale > 0)) return default;
                var fitted = new Size(w * scale, h * scale);
                return new Rect((bounds.Width - fitted.Width) / 2, (bounds.Height - fitted.Height) / 2,
                    fitted.Width, fitted.Height);
            default:
                return new Rect(0, 0, w * PixelDip, h * PixelDip);
        }
    }

    /// <summary>Maps a point in this control's coordinates to a framebuffer pixel, clamped to the desktop.</summary>
    public (int X, int Y) MapToFramebuffer(Point point)
    {
        if (_bitmap is null) return default;
        var dest = DestinationRect();
        if (dest.Width <= 0 || dest.Height <= 0) return default;
        var width = _bitmap.PixelSize.Width;
        var height = _bitmap.PixelSize.Height;
        var x = (int)Math.Floor((point.X - dest.X) * width / dest.Width);
        var y = (int)Math.Floor((point.Y - dest.Y) * height / dest.Height);
        LastPosition = (Math.Clamp(x, 0, width - 1), Math.Clamp(y, 0, height - 1));
        return LastPosition;
    }
}
