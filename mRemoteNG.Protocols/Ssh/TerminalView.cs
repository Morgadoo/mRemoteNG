using System.Globalization;
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
/// scrolls the scrollback (or sends cursor keys on the alternate screen).
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
                bool flush = x == end || row[x].Rune.Value > 0x7E;
                if (flush && text.Length > 0)
                {
                    DrawText(context, text.ToString(), segmentStart, top, typeface, brush);
                    text.Clear();
                }
                if (x == end)
                    break;
                if (row[x].Rune.Value > 0x7E)
                {
                    DrawText(context, row[x].Rune.ToString(), x, top, typeface, brush);
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

    private void DrawText(DrawingContext context, string text, int column, double top, Typeface typeface, IBrush brush)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, TextSize, brush);
        context.DrawText(formatted, new Point(column * _cellWidth, top));
    }

    private void DrawCursor(DrawingContext context)
    {
        var rect = new Rect(_screen.CursorColumn * _cellWidth, _screen.CursorRow * _cellHeight, _cellWidth, _cellHeight);
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
        if (_metricsMeasured)
            return;
        _metricsMeasured = true;

        var probe = new FormattedText(new string('M', 20), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typefaces[0], TextSize, Brushes.White);
        if (probe.WidthIncludingTrailingWhitespace > 0 && probe.Height > 0)
        {
            _cellWidth = probe.WidthIncludingTrailingWhitespace / 20;
            _cellHeight = Math.Ceiling(probe.Height);
            _baseline = probe.Baseline;
        }
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

        int cols = Math.Max(10, (int)(Bounds.Width / _cellWidth));
        int rows = Math.Max(4, (int)(Bounds.Height / _cellHeight));
        if (cols == TerminalCols && rows == TerminalRows)
            return;

        lock (_lock)
            _screen.Resize(cols, rows);
        TerminalCols = cols;
        TerminalRows = rows;
        TerminalResized?.Invoke(this, new TerminalSizeEventArgs(cols, rows, (int)Bounds.Width, (int)Bounds.Height));
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
        if (_selectionAnchor is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        lock (_lock)
            _selectionEnd = HitTest(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
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
            // Full-screen programs have no scrollback; scroll them with cursor keys like xterm's alternateScroll.
            var key = TerminalKeyEncoder.Encode(lines > 0 ? Key.Up : Key.Down, KeyModifiers.None, applicationCursor)!;
            DataToSend?.Invoke(this, Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(key, Math.Abs(lines)))));
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
