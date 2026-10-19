using System.Globalization;
using System.Numerics;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using mRemoteNG.Protocols.Ssh.Terminal;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>Terminal size in character cells and pixels.</summary>
public sealed class TerminalSizeEventArgs(int columns, int rows, int pixelWidth, int pixelHeight) : EventArgs
{
    public int Columns { get; } = columns;
    public int Rows { get; } = rows;
    public int PixelWidth { get; } = pixelWidth;
    public int PixelHeight { get; } = pixelHeight;
}

/// <summary>
/// xterm-compatible terminal control used for SSH, Telnet, rlogin, serial and local shells.
/// Escape sequences are interpreted by <see cref="TerminalScreen"/>; this control renders the
/// screen, turns keyboard/clipboard input into bytes (<see cref="DataToSend"/>) and reports size
/// changes (<see cref="TerminalResized"/>) so the remote side can be told the window size.
///
/// Mouse: drag to select, Ctrl+Shift+C copies, Ctrl+Shift+V / Shift+Insert pastes, the wheel
/// scrolls the scrollback (or sends cursor keys on the alternate screen). When the remote side
/// enables mouse tracking (DECSET 1000/1002/1003), pointer events are reported back in the SGR
/// encoding instead, so full-screen TUIs receive clicks, drags and wheel input.
/// </summary>
public sealed class TerminalView : UserControl
{
    private const double TextSize = 13.0;
    private const double FallbackCellWidth = 8.4;
    private const double FallbackCellHeight = 16.0;
    private const int WheelLines = 3;

    private static readonly FontFamily MonoFamily =
        new("Cascadia Mono, DejaVu Sans Mono, Liberation Mono, Consolas, Menlo, Courier New, monospace");

    private readonly TerminalScreen _screen = new(80, 24);
    private readonly object _lock = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<uint, IBrush> _brushes = [];
    private readonly Typeface[] _typefaces =
    [
        new(MonoFamily, FontStyle.Normal, FontWeight.Normal),
        new(MonoFamily, FontStyle.Normal, FontWeight.Bold),
        new(MonoFamily, FontStyle.Italic, FontWeight.Normal),
        new(MonoFamily, FontStyle.Italic, FontWeight.Bold),
    ];

    private double _cellWidth = FallbackCellWidth;
    private double _cellHeight = FallbackCellHeight;
    private double _baseline = FallbackCellHeight * 0.8;
    private bool _metricsMeasured;
    private long _renderedVersion = -1;
    private int _mouseButtons;
    private int _scrollOffset;
    private bool _suppressNextTextInput;

    // Selection in "absolute" line coordinates: 0 = oldest scrollback line.
    private (int Line, int Column)? _selectionAnchor;
    private (int Line, int Column)? _selectionEnd;

    public TerminalView()
    {
        // No Background: UserControl's template paints it above Render(), hiding the terminal.
        // Render() fills the background itself.
        Focusable = true;

        _screen.Response += reply => DataToSend?.Invoke(this, Encoding.UTF8.GetBytes(reply));

        // Started while attached to the visual tree; redraws only when the screen changed.
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _refreshTimer.Tick += (_, _) =>
        {
            if (_screen.Version != _renderedVersion)
                InvalidateVisual();
        };
    }

    // ── Public API ─────────────────────────────────────────────────────────

    public int TerminalCols { get; private set; } = 80;
    public int TerminalRows { get; private set; } = 24;

    /// <summary>Bytes to send to the remote side (keystrokes, pastes, terminal replies).</summary>
    public event EventHandler<byte[]>? DataToSend;

    /// <summary>Raised on the UI thread when the terminal's size in cells changes.</summary>
    public event EventHandler<TerminalSizeEventArgs>? TerminalResized;

    /// <summary>Window title set by the remote side (OSC 0/2).</summary>
    public string Title
    {
        get { lock (_lock) return _screen.Title; }
    }

    /// <summary>Feeds decoded output from the remote side. Thread-safe.</summary>
    public void Write(string text)
    {
        lock (_lock)
        {
            _screen.Feed(text);
            _selectionAnchor = _selectionEnd = null;
        }
    }

    /// <summary>Current screen contents as text (diagnostics and tests).</summary>
    internal string GetScreenText()
    {
        lock (_lock)
            return _screen.GetScreenText();
    }

    // ── Rendering ──────────────────────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Cursor ??= new Cursor(StandardCursorType.Ibeam);
        MeasureCellSize();
        _refreshTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _refreshTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(BrushFor(TerminalPalette.DefaultBackground), new Rect(Bounds.Size));

        lock (_lock)
        {
            _renderedVersion = _screen.Version;
            int scrollback = _screen.Scrollback.Count;
            _scrollOffset = Math.Min(_scrollOffset, scrollback);

            for (int y = 0; y < _screen.Rows; y++)
            {
                int absolute = scrollback - _scrollOffset + y;
                var row = absolute < scrollback ? _screen.Scrollback[absolute] : _screen.GetRow(absolute - scrollback);
                DrawRow(context, row, y, absolute);
            }

            if (_scrollOffset == 0 && _screen.CursorVisible)
                DrawCursor(context);
        }
    }

    private void DrawRow(DrawingContext context, TerminalCell[] row, int y, int absoluteLine)
    {
        double top = y * _cellHeight;
        int count = Math.Min(row.Length, _screen.Columns);

        // Backgrounds (including the selection highlight)
        for (int start = 0; start < count;)
        {
            uint bg = BackgroundAt(row, start, absoluteLine);
            int end = start + 1;
            while (end < count && BackgroundAt(row, end, absoluteLine) == bg)
                end++;
            if (bg != TerminalPalette.DefaultBackground)
                context.FillRectangle(BrushFor(bg), new Rect(start * _cellWidth, top, (end - start) * _cellWidth, _cellHeight));
            start = end;
        }

        // Text in runs of identical attributes. ASCII is drawn per run; other characters are
        // drawn per cell so fallback-font glyphs cannot shift the rest of the line.
        var text = new StringBuilder();
        for (int start = 0; start < count;)
        {
            var attributes = row[start].Attributes;
            int end = start + 1;
            while (end < count && row[end].Attributes == attributes)
                end++;

            var (fgColor, _) = TerminalPalette.ResolveCell(attributes);
            var brush = BrushFor(fgColor, attributes.Has(TerminalStyle.Dim) ? (byte)0x99 : (byte)0xFF);
            var typeface = _typefaces[(attributes.Has(TerminalStyle.Bold) ? 1 : 0) + (attributes.Has(TerminalStyle.Italic) ? 2 : 0)];

            int segmentStart = start;
            text.Clear();
            for (int x = start; x <= end; x++)
            {
                bool atEnd = x == end;
                bool spacer = !atEnd && row[x].IsSpacer;
                bool nonAscii = !atEnd && !spacer && row[x].Rune.Value > 0x7E;
                if ((atEnd || nonAscii || spacer) && text.Length > 0)
                {
                    DrawText(context, text.ToString(), segmentStart, top, typeface, brush, text.Length);
                    text.Clear();
                }
                if (atEnd)
                    break;
                if (spacer)
                {
                    // The glyph was drawn with the first cell; this one only holds the second column.
                    segmentStart = x + 1;
                    continue;
                }
                if (nonAscii)
                {
                    DrawText(context, row[x].Rune.ToString(), x, top, typeface, brush, Math.Max(1, row[x].Width));
                    segmentStart = x + 1;
                }
                else
                {
                    if (text.Length == 0) segmentStart = x;
                    text.Append((char)row[x].Rune.Value);
                }
            }

            if (attributes.Has(TerminalStyle.Underline))
            {
                double lineY = top + _baseline + 1.5;
                context.DrawLine(new Pen(brush, 1), new Point(start * _cellWidth, lineY), new Point(end * _cellWidth, lineY));
            }
            if (attributes.Has(TerminalStyle.Strikethrough))
            {
                double lineY = top + _cellHeight / 2;
                context.DrawLine(new Pen(brush, 1), new Point(start * _cellWidth, lineY), new Point(end * _cellWidth, lineY));
            }
            start = end;
        }
    }

    /// <summary>
    /// Draws <paramref name="text"/> starting at <paramref name="column"/>, occupying
    /// <paramref name="columns"/> cells. Each glyph is placed on that grid: one
    /// <see cref="FormattedText"/> for a whole run follows the font's own advances, and the
    /// rasterizer rounds each one to a pixel, so a long run ends wider than the cells behind it
    /// and paints over the next run.
    /// </summary>
    private void DrawText(DrawingContext context, string text, int column, double top, Typeface typeface, IBrush brush, int columns)
    {
        if (string.IsNullOrWhiteSpace(text) || columns <= 0)
            return;

        int runes = text.EnumerateRunes().Count();
        if (runes == 0)
            return;

        int index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int cell = column + (int)((long)index * columns / runes);
            var formatted = new FormattedText(rune.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, TextSize, brush);
            context.DrawText(formatted, new Point(cell * _cellWidth, top));
            index++;
        }
    }

    private void DrawCursor(DrawingContext context)
    {
        int span = 1;
        if (_screen.CursorColumn < _screen.Columns && _screen.GetCell(_screen.CursorRow, _screen.CursorColumn).IsWide)
            span = 2;
        var rect = new Rect(_screen.CursorColumn * _cellWidth, _screen.CursorRow * _cellHeight, span * _cellWidth, _cellHeight);
        var brush = BrushFor(TerminalPalette.DefaultForeground, 0xA0);
        if (IsFocused)
            context.FillRectangle(brush, rect);
        else
            context.DrawRectangle(new Pen(brush, 1), rect.Deflate(0.5));
    }

    private uint BackgroundAt(TerminalCell[] row, int column, int absoluteLine)
    {
        if (IsSelected(absoluteLine, column))
            return 0x264F78;
        return TerminalPalette.ResolveCell(row[column].Attributes).Background;
    }

    private IBrush BrushFor(uint rgb, byte alpha = 0xFF)
    {
        uint key = ((uint)alpha << 24) | rgb;
        if (!_brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            _brushes[key] = brush;
        }
        return brush;
    }

    /// <summary>Measures the monospace cell once the control is attached (fonts are available then).</summary>
    private void MeasureCellSize()
    {
        if (!_metricsMeasured)
        {
            var probe = new FormattedText(new string('M', 20), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                _typefaces[0], TextSize, Brushes.White);
            if (probe.WidthIncludingTrailingWhitespace > 0 && probe.Height > 0)
            {
                // Ceil the cell size so glyphs never bleed into the next cell. The grid is then laid
                // out from the exact cell size, which keeps the reported window size honest.
                _cellWidth = Math.Ceiling(probe.WidthIncludingTrailingWhitespace / 20 * 1000) / 1000;
                _cellHeight = Math.Ceiling(probe.Height);
                _baseline = probe.Baseline;
                _metricsMeasured = true;
            }
        }

        // Attachment can happen after a 0-size layout pass. Measure again so the
        // remote side hears the real window size.
        UpdateTerminalSize();
    }

    // ── Size ───────────────────────────────────────────────────────────────

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateTerminalSize();
    }

    private void UpdateTerminalSize()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        // Floor: only report rows/columns that are fully visible, otherwise a TUI paints into a
        // region the view clips and its top rows become unreachable.
        int cols = Math.Max(10, (int)Math.Floor(Bounds.Width / _cellWidth));
        int rows = Math.Max(4, (int)Math.Floor(Bounds.Height / _cellHeight));
        if (cols == TerminalCols && rows == TerminalRows)
            return;

        lock (_lock)
            _screen.Resize(cols, rows);
        TerminalCols = cols;
        TerminalRows = rows;
        TerminalResized?.Invoke(this, new TerminalSizeEventArgs(
            cols, rows, (int)Math.Round(cols * _cellWidth), (int)Math.Round(rows * _cellHeight)));
        InvalidateVisual();
    }

    // ── Keyboard and clipboard ─────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        _suppressNextTextInput = false;
        bool ctrlShift = e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift);

        if ((ctrlShift && e.Key == Key.V) || (e.KeyModifiers == KeyModifiers.Shift && e.Key == Key.Insert))
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }
        if (ctrlShift && e.Key == Key.C)
        {
            _ = CopySelectionAsync();
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers == KeyModifiers.Shift && e.Key is Key.PageUp or Key.PageDown)
        {
            ScrollView(e.Key == Key.PageUp ? TerminalRows - 1 : -(TerminalRows - 1));
            e.Handled = true;
            return;
        }

        bool applicationCursor;
        lock (_lock)
            applicationCursor = _screen.ApplicationCursorKeys;

        var sequence = TerminalKeyEncoder.Encode(e.Key, e.KeyModifiers, applicationCursor);
        if (sequence is not null)
        {
            // Some platforms also raise TextInput for Ctrl/Alt combinations; drop that duplicate.
            _suppressNextTextInput = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != 0;
            Send(sequence);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (_suppressNextTextInput)
        {
            _suppressNextTextInput = false;
            e.Handled = true;
            return;
        }
        if (!string.IsNullOrEmpty(e.Text))
        {
            Send(e.Text);
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    private void Send(string text)
    {
        if (_scrollOffset != 0)
        {
            _scrollOffset = 0;
            InvalidateVisual();
        }
        DataToSend?.Invoke(this, Encoding.UTF8.GetBytes(text));
    }

    private async Task PasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
            return;
        var text = await clipboard.GetTextAsync();
        if (string.IsNullOrEmpty(text))
            return;

        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        bool bracketed;
        lock (_lock)
            bracketed = _screen.BracketedPaste;
        Send(bracketed ? "\x1b[200~" + text.Replace("\x1b", "") + "\x1b[201~" : text);
    }

    private async Task CopySelectionAsync()
    {
        var text = GetSelectedText();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (!string.IsNullOrEmpty(text) && clipboard is not null)
            await clipboard.SetTextAsync(text);
    }

    // ── Mouse: focus, selection, scrolling ─────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);

        if (MouseTrackingActive())
        {
            int button = point.Properties.IsRightButtonPressed ? 2
                : point.Properties.IsMiddleButtonPressed ? 1
                : 0;
            _mouseButtons |= 1 << button;
            ReportMouse(button, point.Position, released: false);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            lock (_lock)
            {
                _selectionAnchor = HitTest(point.Position);
                _selectionEnd = null;
            }
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        else if (point.Properties.IsMiddleButtonPressed)
        {
            _ = PasteAsync();
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetCurrentPoint(this);

        if (MouseTrackingActive())
        {
            bool motion;
            lock (_lock)
                motion = _screen.MouseMoveTracking || (_screen.MouseDragTracking && _mouseButtons != 0);
            if (motion)
            {
                // Button-down motion uses button 0 with the motion bit; bare motion uses 3.
                int button = _mouseButtons != 0 ? BitOperations.TrailingZeroCount(_mouseButtons) : 3;
                ReportMouse(button, point.Position, released: false, motion: true);
            }
            e.Handled = true;
            return;
        }

        if (_selectionAnchor is null || !point.Properties.IsLeftButtonPressed)
            return;
        lock (_lock)
            _selectionEnd = HitTest(point.Position);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (MouseTrackingActive() || _mouseButtons != 0)
        {
            int button = e.InitialPressMouseButton switch
            {
                MouseButton.Right => 2,
                MouseButton.Middle => 1,
                _ => 0,
            };
            _mouseButtons &= ~(1 << button);
            ReportMouse(button, e.GetPosition(this), released: true);
            if (_mouseButtons == 0)
                e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }

        e.Pointer.Capture(null);
    }

    /// <summary>True while the remote side wants pointer events instead of local selection.</summary>
    private bool MouseTrackingActive()
    {
        lock (_lock)
            return _screen.MouseTracking && _screen.MouseSgrEncoding && _scrollOffset == 0;
    }

    /// <summary>Sends one SGR mouse report (CSI &lt; button ; column ; row M/m), 1-based cells.</summary>
    private void ReportMouse(int button, Point position, bool released, bool motion = false)
    {
        int column = Math.Clamp((int)(position.X / _cellWidth), 0, TerminalCols - 1) + 1;
        int row = Math.Clamp((int)(position.Y / _cellHeight), 0, TerminalRows - 1) + 1;
        int code = button + (motion ? 32 : 0);
        Send($"\x1b[<{code};{column};{row}{(released ? "m" : "M")}");
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        int lines = (int)Math.Round(e.Delta.Y * WheelLines);
        if (lines == 0)
            return;

        bool alternate, applicationCursor;
        lock (_lock)
        {
            alternate = _screen.IsAlternateScreen;
            applicationCursor = _screen.ApplicationCursorKeys;
        }

        if (alternate)
        {
            if (MouseTrackingActive())
            {
                // Button 4 scrolls up, button 5 scrolls down (one report per line).
                for (int i = 0; i < Math.Abs(lines); i++)
                    ReportMouse(lines > 0 ? 4 : 5, e.GetPosition(this), released: false);
            }
            else
            {
                // Full-screen programs have no scrollback; scroll them with cursor keys like xterm's alternateScroll.
                var key = TerminalKeyEncoder.Encode(lines > 0 ? Key.Up : Key.Down, KeyModifiers.None, applicationCursor)!;
                DataToSend?.Invoke(this, Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(key, Math.Abs(lines)))));
            }
        }
        else
        {
            ScrollView(lines);
        }
        e.Handled = true;
    }

    private void ScrollView(int lines)
    {
        lock (_lock)
            _scrollOffset = Math.Clamp(_scrollOffset + lines, 0, _screen.Scrollback.Count);
        InvalidateVisual();
    }

    private (int Line, int Column) HitTest(Point position)
    {
        int row = Math.Clamp((int)(position.Y / _cellHeight), 0, _screen.Rows - 1);
        int column = Math.Clamp((int)Math.Round(position.X / _cellWidth), 0, _screen.Columns);
        return (_screen.Scrollback.Count - _scrollOffset + row, column);
    }

    private bool IsSelected(int line, int column)
    {
        if (_selectionAnchor is not { } a || _selectionEnd is not { } b || a == b)
            return false;
        var (start, end) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
        if (line < start.Line || line > end.Line)
            return false;
        if (line == start.Line && column < start.Column)
            return false;
        if (line == end.Line && column >= end.Column)
            return false;
        return true;
    }

    private string GetSelectedText()
    {
        lock (_lock)
        {
            if (_selectionAnchor is not { } a || _selectionEnd is not { } b || a == b)
                return string.Empty;
            var (start, end) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
            int scrollback = _screen.Scrollback.Count;
            var lines = new List<string>();
            for (int line = start.Line; line <= end.Line; line++)
            {
                if (line < 0 || line >= scrollback + _screen.Rows)
                    continue;
                var row = line < scrollback ? _screen.Scrollback[line] : _screen.GetRow(line - scrollback);
                int from = line == start.Line ? Math.Min(start.Column, row.Length) : 0;
                int to = line == end.Line ? Math.Min(end.Column, row.Length) : row.Length;
                lines.Add(TerminalScreen.RowToString(row[from..to]));
            }
            return string.Join(Environment.NewLine, lines);
        }
    }
}
