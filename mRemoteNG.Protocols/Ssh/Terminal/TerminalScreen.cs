using System.Globalization;
using System.Text;

namespace mRemoteNG.Protocols.Ssh.Terminal;

/// <summary>
/// Screen model and escape-sequence parser for an xterm-compatible terminal, independent of any
/// rendering. Feed it the decoded output of the remote side; read the grid, cursor and modes back.
///
/// Supported: C0 controls, SGR (16/256/truecolour, bold, dim, italic, underline, blink, inverse,
/// hidden, strikethrough), cursor movement (CUU/CUD/CUF/CUB/CNL/CPL/CHA/HPA/VPA/CUP), erase
/// (ED/EL/ECH), insert/delete (ICH/DCH/IL/DL), scrolling (SU/SD, IND/RI/NEL), scroll regions
/// (DECSTBM), origin mode, auto-wrap, insert mode, tab stops, DEC line drawing, save/restore
/// cursor, the alternate screen (47/1047/1049), cursor visibility, application cursor keys,
/// bracketed paste, DSR/DA replies, OSC window titles and a scrollback buffer.
///
/// Not thread-safe: callers serialise access (the view locks around feeding and rendering).
/// Characters are one cell wide; East Asian wide characters are not given two cells.
/// </summary>
public sealed class TerminalScreen
{
    private const int MaxParameterValue = 65535;
    private const int MaxStringLength = 4096;

    private enum ParserState
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        Osc,
        OscEscape,
        IgnoredString,
        IgnoredStringEscape,
    }

    private sealed class Buffer
    {
        public TerminalCell[][] Lines = [];
        public SavedCursor? Saved;
    }

    private readonly record struct SavedCursor(
        int Row, int Column, TerminalAttributes Attributes, bool OriginMode, bool WrapPending,
        char G0, char G1, int ActiveCharset);

    private readonly Buffer _main = new();
    private readonly Buffer _alternate = new();
    private Buffer _buffer;
    private readonly List<TerminalCell[]> _scrollback = [];
    private bool[] _tabStops = [];

    // Parser state
    private ParserState _state = ParserState.Ground;
    private readonly StringBuilder _params = new();
    private readonly StringBuilder _intermediates = new();
    private readonly StringBuilder _oscText = new();
    private char _privateMarker;
    private char _pendingHighSurrogate;

    // Terminal state
    private int _scrollTop;
    private int _scrollBottom;
    private bool _wrapPending;
    private bool _originMode;
    private bool _autoWrap = true;
    private bool _insertMode;
    private char _g0 = 'B';
    private char _g1 = 'B';
    private int _activeCharset;
    private Rune _lastPrinted = TerminalCell.Space;

    public TerminalScreen(int columns = 80, int rows = 24, int scrollbackLimit = 1000)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        ScrollbackLimit = Math.Max(0, scrollbackLimit);
        _buffer = _main;
        _main.Lines = NewLines(Rows, Columns, TerminalAttributes.Default);
        _alternate.Lines = NewLines(Rows, Columns, TerminalAttributes.Default);
        _scrollBottom = Rows - 1;
        ResetTabStops();
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int ScrollbackLimit { get; }
    public int CursorRow { get; private set; }
    public int CursorColumn { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool ApplicationCursorKeys { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool IsAlternateScreen => _buffer == _alternate;
    public int ScrollTop => _scrollTop;
    public int ScrollBottom => _scrollBottom;
    public string Title { get; private set; } = string.Empty;
    public TerminalAttributes CurrentAttributes { get; private set; }

    /// <summary>Incremented on every change; renderers compare it to skip redundant redraws.</summary>
    public long Version { get; private set; }

    /// <summary>Lines scrolled off the top of the main screen, oldest first.</summary>
    public IReadOnlyList<TerminalCell[]> Scrollback => _scrollback;

    /// <summary>Replies the terminal must send back to the host (cursor position reports, device attributes).</summary>
    public event Action<string>? Response;

    public event Action? Bell;

    public TerminalCell GetCell(int row, int column) => _buffer.Lines[row][column];

    /// <summary>The live row array; do not keep it beyond the caller's lock.</summary>
    public TerminalCell[] GetRow(int row) => _buffer.Lines[row];

    /// <summary>Text of a screen row with trailing spaces removed.</summary>
    public string GetLineText(int row) => RowToString(_buffer.Lines[row]);

    public static string RowToString(TerminalCell[] cells)
    {
        var sb = new StringBuilder(cells.Length);
        foreach (var cell in cells)
            sb.Append(cell.Rune.ToString());
        return sb.ToString().TrimEnd(' ');
    }

    /// <summary>All screen rows as text, joined with '\n' (for diagnostics and tests).</summary>
    public string GetScreenText() =>
        string.Join('\n', Enumerable.Range(0, Rows).Select(GetLineText));

    // ── Input ──────────────────────────────────────────────────────────────

    public void Feed(string text)
    {
        foreach (char c in text)
            Process(c);
        Version++;
    }

    private void Process(char c)
    {
        switch (_state)
        {
            case ParserState.Ground:
                Ground(c);
                break;
            case ParserState.Escape:
                Escape(c);
                break;
            case ParserState.EscapeIntermediate:
                EscapeIntermediate(c);
                break;
            case ParserState.Csi:
                Csi(c);
                break;
            case ParserState.Osc:
                if (c == '\a') { DispatchOsc(); _state = ParserState.Ground; }
                else if (c == '\x1b') _state = ParserState.OscEscape;
                else if (_oscText.Length < MaxStringLength) _oscText.Append(c);
                break;
            case ParserState.OscEscape:
                // ESC \ (ST) terminates; anything else aborts the string and starts a new escape.
                if (c == '\\') { DispatchOsc(); _state = ParserState.Ground; }
                else { _state = ParserState.Escape; Escape(c); }
                break;
            case ParserState.IgnoredString:
                if (c == '\x1b') _state = ParserState.IgnoredStringEscape;
                else if (c == '\a') _state = ParserState.Ground;
                break;
            case ParserState.IgnoredStringEscape:
                if (c == '\\') _state = ParserState.Ground;
                else { _state = ParserState.Escape; Escape(c); }
                break;
        }
    }

    private void Ground(char c)
    {
        if (c < 0x20)
        {
            Control(c);
            return;
        }
        if (c == 0x7F || (c >= 0x80 && c < 0xA0))
            return;

        if (char.IsHighSurrogate(c))
        {
            _pendingHighSurrogate = c;
            return;
        }

        Rune rune;
        if (char.IsLowSurrogate(c))
        {
            if (_pendingHighSurrogate == 0)
                return;
            rune = new Rune(_pendingHighSurrogate, c);
            _pendingHighSurrogate = '\0';
        }
        else
        {
            rune = new Rune(c);
        }
        Print(rune);
    }

    private void Control(char c)
    {
        switch (c)
        {
            case '\x1b':
                _state = ParserState.Escape;
                _intermediates.Clear();
                break;
            case '\a': Bell?.Invoke(); break;
            case '\b':
                _wrapPending = false;
                if (CursorColumn > 0) CursorColumn--;
                break;
            case '\t': TabForward(1); break;
            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                break;
            case '\r':
                CursorColumn = 0;
                _wrapPending = false;
                break;
            case '\x0E': _activeCharset = 1; break; // SO
            case '\x0F': _activeCharset = 0; break; // SI
        }
    }

    private void Escape(char c)
    {
        _state = ParserState.Ground;
        switch (c)
        {
            case '[':
                _state = ParserState.Csi;
                _params.Clear();
                _intermediates.Clear();
                _privateMarker = '\0';
                break;
            case ']':
                _state = ParserState.Osc;
                _oscText.Clear();
                break;
            case 'P': // DCS
            case 'X': // SOS
            case '^': // PM
            case '_': // APC
                _state = ParserState.IgnoredString;
                break;
            case '(':
            case ')':
            case '*':
            case '+':
            case '#':
            case ' ':
            case '%':
                _intermediates.Clear();
                _intermediates.Append(c);
                _state = ParserState.EscapeIntermediate;
                break;
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': Index(); break;
            case 'E':
                CursorColumn = 0;
                Index();
                break;
            case 'M': ReverseIndex(); break;
            case 'H':
                if (CursorColumn < Columns) _tabStops[CursorColumn] = true;
                break;
            case 'c': Reset(); break;
            case '\x1b':
                _state = ParserState.Escape;
                break;
            default:
                // ESC = / ESC > (keypad modes), ESC \ (stray ST) and unknown escapes are ignored.
                if (c < 0x20)
                    Control(c);
                break;
        }
    }

    private void EscapeIntermediate(char c)
    {
        _state = ParserState.Ground;
        if (c < 0x20)
        {
            Control(c);
            return;
        }

        switch (_intermediates.Length > 0 ? _intermediates[0] : '\0')
        {
            case '(': _g0 = c; break;
            case ')': _g1 = c; break;
            case '#':
                if (c == '8') FillWithE(); // DECALN screen alignment test
                break;
        }
    }

    private void Csi(char c)
    {
        if (c >= '0' && c <= '9' || c == ';' || c == ':')
        {
            if (_params.Length < 256) _params.Append(c);
        }
        else if (c is '?' or '>' or '<' or '=')
        {
            if (_params.Length == 0) _privateMarker = c;
        }
        else if (c >= 0x20 && c <= 0x2F)
        {
            _intermediates.Append(c);
        }
        else if (c >= 0x40 && c <= 0x7E)
        {
            _state = ParserState.Ground;
            DispatchCsi(c, ParseParameters(_params.ToString()));
        }
        else if (c == '\x1b')
        {
            _state = ParserState.Escape;
        }
        else if (c < 0x20)
        {
            Control(c); // C0 controls execute in the middle of a CSI sequence
        }
        else
        {
            _state = ParserState.Ground; // malformed
        }
    }

    /// <summary>Parameters as groups of colon-separated sub-parameters; -1 marks an omitted value.</summary>
    private static List<int[]> ParseParameters(string text)
    {
        var result = new List<int[]>();
        if (text.Length == 0)
            return result;

        foreach (var group in text.Split(';'))
        {
            var parts = group.Split(':');
            var values = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                values[i] = parts[i].Length == 0
                    ? -1
                    : int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v)
                        ? Math.Min(v, MaxParameterValue)
                        : MaxParameterValue;
            }
            result.Add(values);
        }
        return result;
    }

    /// <summary>Parameter <paramref name="index"/>, with omitted or zero values replaced by <paramref name="defaultValue"/>.</summary>
    private static int Param(List<int[]> p, int index, int defaultValue = 1)
    {
        if (index >= p.Count) return defaultValue;
        int v = p[index][0];
        return v <= 0 ? defaultValue : v;
    }

    private void DispatchCsi(char final, List<int[]> p)
    {
        string intermediates = _intermediates.ToString();

        if (_privateMarker == '?')
        {
            if (final is 'h' or 'l')
            {
                foreach (var group in p)
                    SetPrivateMode(group[0], final == 'h');
            }
            return;
        }

        if (_privateMarker == '>')
        {
            if (final == 'c')
                Response?.Invoke("\x1b[>0;10;0c");
            return;
        }

        if (_privateMarker != '\0')
            return;

        if (intermediates == "!" && final == 'p')
        {
            SoftReset();
            return;
        }
        if (intermediates.Length > 0)
            return; // DECSCUSR (" q") and other intermediate sequences are not rendered

        if (final != 'm' && final != 'b')
            _wrapPending = false;

        switch (final)
        {
            case '@': InsertCharacters(Param(p, 0)); break;
            case 'A': CursorUp(Param(p, 0)); break;
            case 'B':
            case 'e':
                CursorDown(Param(p, 0));
                break;
            case 'C':
            case 'a':
                CursorColumn = Math.Min(Columns - 1, CursorColumn + Param(p, 0));
                break;
            case 'D': CursorColumn = Math.Max(0, CursorColumn - Param(p, 0)); break;
            case 'E':
                CursorDown(Param(p, 0));
                CursorColumn = 0;
                break;
            case 'F':
                CursorUp(Param(p, 0));
                CursorColumn = 0;
                break;
            case 'G':
            case '`':
                CursorColumn = Math.Clamp(Param(p, 0) - 1, 0, Columns - 1);
                break;
            case 'H':
            case 'f':
                SetCursorPosition(Param(p, 0) - 1, Param(p, 1) - 1);
                break;
            case 'I': TabForward(Param(p, 0)); break;
            case 'J': EraseInDisplay(Param(p, 0, 0)); break;
            case 'K': EraseInLine(Param(p, 0, 0)); break;
            case 'L': InsertLines(Param(p, 0)); break;
            case 'M': DeleteLines(Param(p, 0)); break;
            case 'P': DeleteCharacters(Param(p, 0)); break;
            case 'S': ScrollUp(Param(p, 0)); break;
            case 'T':
                if (p.Count <= 1) ScrollDown(Param(p, 0)); // with more parameters it is mouse tracking
                break;
            case 'X': EraseCharacters(Param(p, 0)); break;
            case 'Z': TabBackward(Param(p, 0)); break;
            case 'b':
                for (int i = Math.Min(Param(p, 0), Columns * Rows); i > 0; i--)
                    Print(_lastPrinted);
                break;
            case 'c':
                if (Param(p, 0, 0) == 0)
                    Response?.Invoke("\x1b[?1;2c");
                break;
            case 'd':
                SetCursorPosition(Param(p, 0) - 1, CursorColumn, keepColumn: true);
                break;
            case 'g':
                ClearTabStops(Param(p, 0, 0));
                break;
            case 'h':
            case 'l':
                foreach (var group in p)
                {
                    if (group[0] == 4) _insertMode = final == 'h';
                }
                break;
            case 'm': SelectGraphicRendition(p); break;
            case 'n': DeviceStatusReport(Param(p, 0, 0)); break;
            case 'r': SetScrollRegion(Param(p, 0) - 1, Param(p, 1, Rows) - 1); break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
        }
    }

    private void DispatchOsc()
    {
        var text = _oscText.ToString();
        int separator = text.IndexOf(';');
        if (separator <= 0)
            return;
        if (text[..separator] is "0" or "2")
            Title = text[(separator + 1)..];
    }

    // ── SGR ────────────────────────────────────────────────────────────────

    private void SelectGraphicRendition(List<int[]> p)
    {
        if (p.Count == 0)
        {
            CurrentAttributes = TerminalAttributes.Default;
            return;
        }

        var a = CurrentAttributes;
        for (int i = 0; i < p.Count; i++)
        {
            int[] group = p[i];
            int code = Math.Max(group[0], 0);
            switch (code)
            {
                case 0: a = TerminalAttributes.Default; break;
                case 1: a = a.With(TerminalStyle.Bold, true); break;
                case 2: a = a.With(TerminalStyle.Dim, true); break;
                case 3: a = a.With(TerminalStyle.Italic, true); break;
                case 4:
                    // 4:0 turns underline off; 4:1..4:5 are underline styles, drawn as a single line.
                    a = a.With(TerminalStyle.Underline, group.Length < 2 || group[1] != 0);
                    break;
                case 5:
                case 6:
                    a = a.With(TerminalStyle.Blink, true);
                    break;
                case 7: a = a.With(TerminalStyle.Inverse, true); break;
                case 8: a = a.With(TerminalStyle.Hidden, true); break;
                case 9: a = a.With(TerminalStyle.Strikethrough, true); break;
                case 21: a = a.With(TerminalStyle.Underline, true); break;
                case 22: a = a.With(TerminalStyle.Bold | TerminalStyle.Dim, false); break;
                case 23: a = a.With(TerminalStyle.Italic, false); break;
                case 24: a = a.With(TerminalStyle.Underline, false); break;
                case 25: a = a.With(TerminalStyle.Blink, false); break;
                case 27: a = a.With(TerminalStyle.Inverse, false); break;
                case 28: a = a.With(TerminalStyle.Hidden, false); break;
                case 29: a = a.With(TerminalStyle.Strikethrough, false); break;
                case >= 30 and <= 37: a = a with { Foreground = TerminalColor.Indexed(code - 30) }; break;
                case 38:
                    if (TryParseExtendedColor(p, ref i, out var fg)) a = a with { Foreground = fg };
                    break;
                case 39: a = a with { Foreground = TerminalColor.Default }; break;
                case >= 40 and <= 47: a = a with { Background = TerminalColor.Indexed(code - 40) }; break;
                case 48:
                    if (TryParseExtendedColor(p, ref i, out var bg)) a = a with { Background = bg };
                    break;
                case 49: a = a with { Background = TerminalColor.Default }; break;
                case 58:
                    TryParseExtendedColor(p, ref i, out _); // underline colour: parsed and ignored
                    break;
                case >= 90 and <= 97: a = a with { Foreground = TerminalColor.Indexed(code - 90 + 8) }; break;
                case >= 100 and <= 107: a = a with { Background = TerminalColor.Indexed(code - 100 + 8) }; break;
            }
        }
        CurrentAttributes = a;
    }

    /// <summary>
    /// Parses 38/48/58 colour arguments in either the semicolon form (<c>38;5;n</c>, <c>38;2;r;g;b</c>)
    /// or the colon form (<c>38:5:n</c>, <c>38:2:r:g:b</c>, <c>38:2:cs:r:g:b</c>).
    /// </summary>
    private static bool TryParseExtendedColor(List<int[]> p, ref int i, out TerminalColor color)
    {
        color = TerminalColor.Default;
        int[] group = p[i];

        if (group.Length > 1)
        {
            int mode = group[1];
            if (mode == 5 && group.Length >= 3)
            {
                color = TerminalColor.Indexed(Math.Max(group[2], 0));
                return true;
            }
            if (mode == 2 && group.Length >= 5)
            {
                int offset = group.Length >= 6 ? 3 : 2; // optional colour-space id
                color = TerminalColor.Rgb(Math.Max(group[offset], 0), Math.Max(group[offset + 1], 0), Math.Max(group[offset + 2], 0));
                return true;
            }
            return false;
        }

        if (i + 1 >= p.Count)
            return false;
        int kind = p[i + 1][0];
        if (kind == 5 && i + 2 < p.Count)
        {
            color = TerminalColor.Indexed(Math.Max(p[i + 2][0], 0));
            i += 2;
            return true;
        }
        if (kind == 2 && i + 4 < p.Count)
        {
            color = TerminalColor.Rgb(Math.Max(p[i + 2][0], 0), Math.Max(p[i + 3][0], 0), Math.Max(p[i + 4][0], 0));
            i += 4;
            return true;
        }
        i = p.Count; // unknown colour model: the rest of the sequence cannot be interpreted
        return false;
    }

    // ── Modes ──────────────────────────────────────────────────────────────

    private void SetPrivateMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: ApplicationCursorKeys = on; break;
            case 6:
                _originMode = on;
                SetCursorPosition(0, 0);
                break;
            case 7: _autoWrap = on; break;
            case 25: CursorVisible = on; break;
            case 47:
            case 1047:
                SwitchScreen(on, clearAlternate: mode == 1047 && on);
                break;
            case 1048:
                if (on) SaveCursor();
                else RestoreCursor();
                break;
            case 1049:
                if (on)
                {
                    SaveCursor();
                    SwitchScreen(true, clearAlternate: true);
                }
                else
                {
                    SwitchScreen(false, clearAlternate: false);
                    RestoreCursor();
                }
                break;
            case 2004: BracketedPaste = on; break;
        }
    }

    private void SwitchScreen(bool alternate, bool clearAlternate)
    {
        var target = alternate ? _alternate : _main;
        if (target == _buffer)
            return;

        // Each screen keeps its own saved cursor, so 1049 restores what was saved on the main screen.
        _buffer = target;
        if (clearAlternate)
            _alternate.Lines = NewLines(Rows, Columns, TerminalAttributes.Default);
        _wrapPending = false;
    }

    // ── Printing ───────────────────────────────────────────────────────────

    private void Print(Rune rune)
    {
        if ((_activeCharset == 0 ? _g0 : _g1) == '0')
            rune = MapDecSpecialGraphics(rune);

        if (_wrapPending && _autoWrap)
        {
            CursorColumn = 0;
            Index();
        }
        _wrapPending = false;

        var line = _buffer.Lines[CursorRow];
        if (_insertMode)
            Array.Copy(line, CursorColumn, line, CursorColumn + 1, Columns - CursorColumn - 1);
        line[CursorColumn] = new TerminalCell(rune, CurrentAttributes);
        _lastPrinted = rune;

        if (CursorColumn == Columns - 1)
            _wrapPending = _autoWrap;
        else
            CursorColumn++;
    }

    private static Rune MapDecSpecialGraphics(Rune rune)
    {
        if (rune.Value < 0x5F || rune.Value > 0x7E)
            return rune;
        const string map = " ◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";
        return new Rune(map[rune.Value - 0x5F]);
    }

    // ── Cursor movement ────────────────────────────────────────────────────

    private void SetCursorPosition(int row, int column, bool keepColumn = false)
    {
        _wrapPending = false;
        if (_originMode)
            CursorRow = Math.Clamp(row + _scrollTop, _scrollTop, _scrollBottom);
        else
            CursorRow = Math.Clamp(row, 0, Rows - 1);
        if (!keepColumn)
            CursorColumn = Math.Clamp(column, 0, Columns - 1);
    }

    private void CursorUp(int n)
    {
        int limit = CursorRow >= _scrollTop ? _scrollTop : 0;
        CursorRow = Math.Max(limit, CursorRow - n);
    }

    private void CursorDown(int n)
    {
        int limit = CursorRow <= _scrollBottom ? _scrollBottom : Rows - 1;
        CursorRow = Math.Min(limit, CursorRow + n);
    }

    private void LineFeed()
    {
        _wrapPending = false;
        Index();
    }

    /// <summary>IND: move down one line, scrolling the region when at its bottom margin.</summary>
    private void Index()
    {
        if (CursorRow == _scrollBottom)
            ScrollUp(1);
        else if (CursorRow < Rows - 1)
            CursorRow++;
    }

    /// <summary>RI: move up one line, scrolling the region down when at its top margin.</summary>
    private void ReverseIndex()
    {
        _wrapPending = false;
        if (CursorRow == _scrollTop)
            ScrollDown(1);
        else if (CursorRow > 0)
            CursorRow--;
    }

    private void TabForward(int count)
    {
        _wrapPending = false;
        for (int n = 0; n < count && CursorColumn < Columns - 1; n++)
        {
            do CursorColumn++;
            while (CursorColumn < Columns - 1 && !_tabStops[CursorColumn]);
        }
    }

    private void TabBackward(int count)
    {
        for (int n = 0; n < count && CursorColumn > 0; n++)
        {
            do CursorColumn--;
            while (CursorColumn > 0 && !_tabStops[CursorColumn]);
        }
    }

    private void ClearTabStops(int mode)
    {
        if (mode == 0 && CursorColumn < Columns)
            _tabStops[CursorColumn] = false;
        else if (mode == 3)
            Array.Clear(_tabStops);
    }

    private void ResetTabStops()
    {
        _tabStops = new bool[Columns];
        for (int i = 8; i < Columns; i += 8)
            _tabStops[i] = true;
    }

    private void SaveCursor() =>
        _buffer.Saved = new SavedCursor(CursorRow, CursorColumn, CurrentAttributes, _originMode, _wrapPending,
            _g0, _g1, _activeCharset);

    private void RestoreCursor()
    {
        if (_buffer.Saved is not { } s)
        {
            CursorRow = 0;
            CursorColumn = 0;
            CurrentAttributes = TerminalAttributes.Default;
            _wrapPending = false;
            return;
        }
        CursorRow = Math.Clamp(s.Row, 0, Rows - 1);
        CursorColumn = Math.Clamp(s.Column, 0, Columns - 1);
        CurrentAttributes = s.Attributes;
        _originMode = s.OriginMode;
        _wrapPending = s.WrapPending;
        _g0 = s.G0;
        _g1 = s.G1;
        _activeCharset = s.ActiveCharset;
    }

    private void SetScrollRegion(int top, int bottom)
    {
        bottom = Math.Min(bottom, Rows - 1);
        if (top < 0 || top >= bottom)
            return;
        _scrollTop = top;
        _scrollBottom = bottom;
        SetCursorPosition(0, 0);
    }

    // ── Erasing and editing ────────────────────────────────────────────────

    /// <summary>Blank cell carrying the current background colour (xterm's background colour erase).</summary>
    private TerminalCell ErasedCell() =>
        TerminalCell.Blank(new TerminalAttributes(TerminalColor.Default, CurrentAttributes.Background, TerminalStyle.None));

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseInLine(0);
                for (int r = CursorRow + 1; r < Rows; r++) FillRow(r, 0, Columns);
                break;
            case 1:
                EraseInLine(1);
                for (int r = 0; r < CursorRow; r++) FillRow(r, 0, Columns);
                break;
            case 2:
                for (int r = 0; r < Rows; r++) FillRow(r, 0, Columns);
                break;
            case 3:
                for (int r = 0; r < Rows; r++) FillRow(r, 0, Columns);
                _scrollback.Clear();
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0: FillRow(CursorRow, CursorColumn, Columns); break;
            case 1: FillRow(CursorRow, 0, CursorColumn + 1); break;
            case 2: FillRow(CursorRow, 0, Columns); break;
        }
    }

    private void EraseCharacters(int n) =>
        FillRow(CursorRow, CursorColumn, Math.Min(Columns, CursorColumn + n));

    private void FillRow(int row, int from, int to)
    {
        var cell = ErasedCell();
        Array.Fill(_buffer.Lines[row], cell, from, Math.Max(0, to - from));
    }

    private void InsertCharacters(int n)
    {
        var line = _buffer.Lines[CursorRow];
        n = Math.Min(n, Columns - CursorColumn);
        Array.Copy(line, CursorColumn, line, CursorColumn + n, Columns - CursorColumn - n);
        Array.Fill(line, ErasedCell(), CursorColumn, n);
    }

    private void DeleteCharacters(int n)
    {
        var line = _buffer.Lines[CursorRow];
        n = Math.Min(n, Columns - CursorColumn);
        Array.Copy(line, CursorColumn + n, line, CursorColumn, Columns - CursorColumn - n);
        Array.Fill(line, ErasedCell(), Columns - n, n);
    }

    private void InsertLines(int n)
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom)
            return;
        ScrollRegionDown(CursorRow, _scrollBottom, n);
        CursorColumn = 0;
    }

    private void DeleteLines(int n)
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom)
            return;
        ScrollRegionUp(CursorRow, _scrollBottom, n, toScrollback: false);
        CursorColumn = 0;
    }

    private void ScrollUp(int n) =>
        ScrollRegionUp(_scrollTop, _scrollBottom, n, toScrollback: _scrollTop == 0 && !IsAlternateScreen);

    private void ScrollDown(int n) => ScrollRegionDown(_scrollTop, _scrollBottom, n);

    private void ScrollRegionUp(int top, int bottom, int n, bool toScrollback)
    {
        var lines = _buffer.Lines;
        n = Math.Min(n, bottom - top + 1);
        if (toScrollback && ScrollbackLimit > 0)
        {
            for (int i = 0; i < n; i++)
                _scrollback.Add(lines[top + i]);
            if (_scrollback.Count > ScrollbackLimit)
                _scrollback.RemoveRange(0, _scrollback.Count - ScrollbackLimit);
        }
        Array.Copy(lines, top + n, lines, top, bottom - top + 1 - n);
        for (int r = bottom - n + 1; r <= bottom; r++)
            lines[r] = NewLine(Columns, ErasedCell());
    }

    private void ScrollRegionDown(int top, int bottom, int n)
    {
        var lines = _buffer.Lines;
        n = Math.Min(n, bottom - top + 1);
        Array.Copy(lines, top, lines, top + n, bottom - top + 1 - n);
        for (int r = top; r < top + n; r++)
            lines[r] = NewLine(Columns, ErasedCell());
    }

    private void FillWithE()
    {
        foreach (var line in _buffer.Lines)
            Array.Fill(line, new TerminalCell(new Rune('E'), TerminalAttributes.Default));
    }

    private void DeviceStatusReport(int code)
    {
        if (code == 5)
        {
            Response?.Invoke("\x1b[0n");
        }
        else if (code == 6)
        {
            int row = _originMode ? CursorRow - _scrollTop : CursorRow;
            Response?.Invoke($"\x1b[{row + 1};{CursorColumn + 1}R");
        }
    }

    // ── Reset and resize ───────────────────────────────────────────────────

    private void SoftReset()
    {
        CursorVisible = true;
        _originMode = false;
        _autoWrap = true;
        _insertMode = false;
        ApplicationCursorKeys = false;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        CurrentAttributes = TerminalAttributes.Default;
        _g0 = _g1 = 'B';
        _activeCharset = 0;
        _wrapPending = false;
        _buffer.Saved = null;
    }

    /// <summary>RIS: full reset to the initial state (scrollback is kept).</summary>
    public void Reset()
    {
        SoftReset();
        BracketedPaste = false;
        _buffer = _main;
        _main.Saved = _alternate.Saved = null;
        _main.Lines = NewLines(Rows, Columns, TerminalAttributes.Default);
        _alternate.Lines = NewLines(Rows, Columns, TerminalAttributes.Default);
        CursorRow = CursorColumn = 0;
        Title = string.Empty;
        ResetTabStops();
        Version++;
    }

    /// <summary>
    /// Changes the screen size. Content keeps its top-left anchoring; when the main screen shrinks,
    /// lines above the cursor move into the scrollback so the cursor line stays visible.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows)
            return;

        int pushTop = Math.Max(0, CursorRow + 1 - rows);
        _main.Lines = ResizeLines(_main.Lines, columns, rows, IsAlternateScreen ? 0 : pushTop, toScrollback: true);
        _alternate.Lines = ResizeLines(_alternate.Lines, columns, rows, IsAlternateScreen ? pushTop : 0, toScrollback: false);

        CursorRow = Math.Clamp(CursorRow - pushTop, 0, rows - 1);
        CursorColumn = Math.Clamp(CursorColumn, 0, columns - 1);
        Columns = columns;
        Rows = rows;
        _scrollTop = 0;
        _scrollBottom = rows - 1;
        _wrapPending = false;
        ResetTabStops();
        Version++;
    }

    private TerminalCell[][] ResizeLines(TerminalCell[][] lines, int columns, int rows, int dropTop, bool toScrollback)
    {
        var result = new TerminalCell[rows][];
        if (toScrollback && ScrollbackLimit > 0)
        {
            for (int r = 0; r < dropTop && r < lines.Length; r++)
                _scrollback.Add(lines[r]);
            if (_scrollback.Count > ScrollbackLimit)
                _scrollback.RemoveRange(0, _scrollback.Count - ScrollbackLimit);
        }

        for (int r = 0; r < rows; r++)
        {
            int source = r + dropTop;
            var line = NewLine(columns, TerminalCell.Blank(TerminalAttributes.Default));
            if (source < lines.Length)
                Array.Copy(lines[source], line, Math.Min(columns, lines[source].Length));
            result[r] = line;
        }
        return result;
    }

    private static TerminalCell[][] NewLines(int rows, int columns, TerminalAttributes attributes)
    {
        var lines = new TerminalCell[rows][];
        for (int r = 0; r < rows; r++)
            lines[r] = NewLine(columns, TerminalCell.Blank(attributes));
        return lines;
    }

    private static TerminalCell[] NewLine(int columns, TerminalCell fill)
    {
        var line = new TerminalCell[columns];
        Array.Fill(line, fill);
        return line;
    }
}
