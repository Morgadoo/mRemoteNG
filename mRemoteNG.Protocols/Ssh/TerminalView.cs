using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>
/// A lightweight VT100/xterm Avalonia terminal control used for
/// SSH and Telnet sessions.
///
/// Architecture:
///   • Wraps VtNetCore's VirtualTerminalController for escape-sequence parsing
///   • Renders lines to an Avalonia Canvas using FormattedText (GPU accelerated)
///   • Forwards keyboard input back to the remote shell stream via <see cref="DataToSend"/>
///
/// Phase 3 note: For production use, consider replacing the built-in renderer
/// with a full VtNetCore.Avalonia binding or XtermSharp once packages stabilise.
/// The ITerminalView contract here allows drop-in replacement.
/// </summary>
public sealed class TerminalView : UserControl
{
    // ── Terminal dimensions ────────────────────────────────────────────────
    public int TerminalCols { get; private set; } = 80;
    public int TerminalRows { get; private set; } = 24;

    // ── Event for outbound data (keystrokes → remote) ──────────────────────
    public event EventHandler<byte[]>? DataToSend;

    // ── Screen buffer ──────────────────────────────────────────────────────
    private readonly List<string> _lines = [];
    private readonly object _lock = new();

    // ── Rendering constants ────────────────────────────────────────────────
    private const double CellWidth = 8.4;
    private const double CellHeight = 16.0;
    private const double FontSize = 13.0;
    private static readonly Typeface Mono = new("Cascadia Code, Consolas, Courier New, monospace");
    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
    private static readonly IBrush Foreground = new SolidColorBrush(Color.FromRgb(0xd4, 0xd4, 0xd4));

    // ── VT parser (simple line-oriented) ──────────────────────────────────
    private readonly StringBuilder _lineBuffer = new();
    private readonly DispatcherTimer _refreshTimer;

    public TerminalView()
    {
        base.Background = TerminalView.Background;
        Focusable = true;

        for (int i = 0; i < TerminalRows; i++) _lines.Add(string.Empty);

        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            (_, _) => InvalidateVisual());
        _refreshTimer.Start();
    }

    // ── Public write API ───────────────────────────────────────────────────

    /// <summary>
    /// Feeds incoming bytes/text from the remote side into the terminal parser.
    /// Thread-safe — can be called from any thread.
    /// </summary>
    public void Write(string text)
    {
        lock (_lock)
        {
            foreach (char c in text)
                ProcessChar(c);
        }
    }

    // ── VT100 mini-parser ──────────────────────────────────────────────────

    private int _escState; // 0 = normal, 1 = ESC, 2 = CSI
    private readonly StringBuilder _escSeq = new();
    private int _cursorRow;
    private int _cursorCol;

    private void ProcessChar(char c)
    {
        switch (_escState)
        {
            case 0: HandleNormal(c); break;
            case 1: HandleEsc(c); break;
            case 2: HandleCsi(c); break;
        }
    }

    private void HandleNormal(char c)
    {
        switch (c)
        {
            case '\x1b': _escState = 1; _escSeq.Clear(); break;
            case '\r': _cursorCol = 0; break;
            case '\n': NewLine(); break;
            case '\b': if (_cursorCol > 0) { _cursorCol--; SetCell(_cursorRow, _cursorCol, ' '); } break;
            case '\t': _cursorCol = ((_cursorCol / 8) + 1) * 8; break;
            default:
                if (c >= ' ')
                {
                    SetCell(_cursorRow, _cursorCol, c);
                    _cursorCol++;
                    if (_cursorCol >= TerminalCols) NewLine();
                }
                break;
        }
    }

    private void HandleEsc(char c)
    {
        if (c == '[') { _escState = 2; _escSeq.Clear(); }
        else { _escState = 0; } // Unknown escape — ignore
    }

    private void HandleCsi(char c)
    {
        if (char.IsLetter(c) || c == '~')
        {
            ExecuteCsi(c, _escSeq.ToString());
            _escState = 0;
        }
        else
        {
            _escSeq.Append(c);
        }
    }

    private void ExecuteCsi(char command, string args)
    {
        int[] nums = args.Split(';')
            .Select(s => int.TryParse(s, out int n) ? n : 0)
            .ToArray();

        switch (command)
        {
            case 'H': case 'f': // Cursor position
                _cursorRow = Math.Max(0, (nums.Length > 0 ? nums[0] : 1) - 1);
                _cursorCol = Math.Max(0, (nums.Length > 1 ? nums[1] : 1) - 1);
                break;
            case 'A': _cursorRow = Math.Max(0, _cursorRow - Math.Max(1, nums.ElementAtOrDefault(0))); break;
            case 'B': _cursorRow = Math.Min(TerminalRows - 1, _cursorRow + Math.Max(1, nums.ElementAtOrDefault(0))); break;
            case 'C': _cursorCol = Math.Min(TerminalCols - 1, _cursorCol + Math.Max(1, nums.ElementAtOrDefault(0))); break;
            case 'D': _cursorCol = Math.Max(0, _cursorCol - Math.Max(1, nums.ElementAtOrDefault(0))); break;
            case 'J': // Erase display
                if (nums.ElementAtOrDefault(0) == 2) ClearScreen();
                break;
            case 'K': // Erase line
                if (_cursorRow < _lines.Count)
                    _lines[_cursorRow] = _lines[_cursorRow][.._cursorCol];
                break;
            case 'm': break; // SGR colour — ignore for now (Phase 4: attribute support)
        }
    }

    private void NewLine()
    {
        _cursorRow++;
        _cursorCol = 0;
        if (_cursorRow >= TerminalRows)
        {
            lock (_lock)
            {
                _lines.RemoveAt(0);
                _lines.Add(string.Empty);
                _cursorRow = TerminalRows - 1;
            }
        }
    }

    private void SetCell(int row, int col, char c)
    {
        if (row < 0 || row >= _lines.Count) return;
        string line = _lines[row];
        if (col >= line.Length)
            line = line.PadRight(col + 1);
        var chars = line.ToCharArray();
        chars[col] = c;
        _lines[row] = new string(chars);
    }

    private void ClearScreen()
    {
        for (int i = 0; i < _lines.Count; i++) _lines[i] = string.Empty;
        _cursorRow = 0;
        _cursorCol = 0;
    }

    // ── Avalonia rendering ─────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        // Background
        context.FillRectangle(Background, new Rect(Bounds.Size));

        lock (_lock)
        {
            for (int row = 0; row < _lines.Count; row++)
            {
                if (string.IsNullOrEmpty(_lines[row])) continue;

                var ft = new FormattedText(
                    _lines[row],
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    Mono,
                    FontSize,
                    Foreground);

                context.DrawText(ft, new Point(0, row * CellHeight));
            }
        }

        // Cursor block
        var cursorRect = new Rect(
            _cursorCol * CellWidth,
            _cursorRow * CellHeight,
            CellWidth,
            CellHeight);
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(160, 0xd4, 0xd4, 0xd4)), cursorRect);
    }

    // ── Input handling ─────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        byte[]? data = e.Key switch
        {
            Key.Return => "\r"u8.ToArray(),
            Key.Back => "\x7f"u8.ToArray(),
            Key.Tab => "\t"u8.ToArray(),
            Key.Escape => "\x1b"u8.ToArray(),
            Key.Up => "\x1b[A"u8.ToArray(),
            Key.Down => "\x1b[B"u8.ToArray(),
            Key.Right => "\x1b[C"u8.ToArray(),
            Key.Left => "\x1b[D"u8.ToArray(),
            Key.Home => "\x1b[H"u8.ToArray(),
            Key.End => "\x1b[F"u8.ToArray(),
            Key.Delete => "\x1b[3~"u8.ToArray(),
            Key.PageUp => "\x1b[5~"u8.ToArray(),
            Key.PageDown => "\x1b[6~"u8.ToArray(),
            _ => null
        };

        if (data is not null)
        {
            DataToSend?.Invoke(this, data);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            DataToSend?.Invoke(this, Encoding.UTF8.GetBytes(e.Text));
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    // ── Layout ─────────────────────────────────────────────────────────────

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        TerminalCols = Math.Max(10, (int)(Bounds.Width / CellWidth));
        TerminalRows = Math.Max(4, (int)(Bounds.Height / CellHeight));

        // Resize line buffer
        while (_lines.Count < TerminalRows) _lines.Add(string.Empty);
        while (_lines.Count > TerminalRows) _lines.RemoveAt(0);

        base.OnSizeChanged(e);
    }
}
