using Avalonia.Input;

namespace mRemoteNG.Protocols.Ssh.Terminal;

/// <summary>
/// Translates key presses that do not produce text into xterm input sequences
/// (cursor/editing/function keys, Ctrl and Alt combinations).
/// Printable text arrives through text input and is sent as UTF-8 by the view.
/// </summary>
public static class TerminalKeyEncoder
{
    /// <summary>Returns the sequence to send, or null when the key should be left to text input.</summary>
    public static string? Encode(Key key, KeyModifiers modifiers, bool applicationCursorKeys)
    {
        bool shift = modifiers.HasFlag(KeyModifiers.Shift);
        bool alt = modifiers.HasFlag(KeyModifiers.Alt);
        bool ctrl = modifiers.HasFlag(KeyModifiers.Control);

        // xterm modifier parameter: 1 + Shift(1) + Alt(2) + Ctrl(4)
        int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

        switch (key)
        {
            case Key.Enter: return alt ? "\x1b\r" : "\r";
            case Key.Back: return (alt ? "\x1b" : "") + (ctrl ? "\b" : "\x7f");
            case Key.Tab: return shift ? "\x1b[Z" : "\t";
            case Key.Escape: return "\x1b";
            case Key.Up: return Cursor('A', mod, applicationCursorKeys);
            case Key.Down: return Cursor('B', mod, applicationCursorKeys);
            case Key.Right: return Cursor('C', mod, applicationCursorKeys);
            case Key.Left: return Cursor('D', mod, applicationCursorKeys);
            case Key.Home: return Cursor('H', mod, applicationCursorKeys);
            case Key.End: return Cursor('F', mod, applicationCursorKeys);
            case Key.Insert: return Tilde(2, mod);
            case Key.Delete: return Tilde(3, mod);
            case Key.PageUp: return Tilde(5, mod);
            case Key.PageDown: return Tilde(6, mod);
            case Key.F1: return Ss3('P', mod);
            case Key.F2: return Ss3('Q', mod);
            case Key.F3: return Ss3('R', mod);
            case Key.F4: return Ss3('S', mod);
            case Key.F5: return Tilde(15, mod);
            case Key.F6: return Tilde(17, mod);
            case Key.F7: return Tilde(18, mod);
            case Key.F8: return Tilde(19, mod);
            case Key.F9: return Tilde(20, mod);
            case Key.F10: return Tilde(21, mod);
            case Key.F11: return Tilde(23, mod);
            case Key.F12: return Tilde(24, mod);
        }

        // Ctrl+Alt is AltGr on many keyboards: leave it to text input.
        if (ctrl && !alt)
        {
            if (key >= Key.A && key <= Key.Z)
                return ((char)(key - Key.A + 1)).ToString();
            return key switch
            {
                Key.Space or Key.D2 => "\0",
                Key.OemOpenBrackets or Key.D3 => "\x1b",
                Key.OemPipe or Key.OemBackslash or Key.D4 => "\x1c",
                Key.OemCloseBrackets or Key.D5 => "\x1d",
                Key.D6 => "\x1e",
                Key.OemMinus or Key.OemQuestion or Key.D7 => "\x1f",
                Key.D8 => "\x7f",
                _ => null,
            };
        }

        // Alt+letter/digit sends ESC prefix ("meta sends escape").
        if (alt && !ctrl)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                char c = (char)((shift ? 'A' : 'a') + (key - Key.A));
                return "\x1b" + c;
            }
            if (key >= Key.D0 && key <= Key.D9 && !shift)
                return "\x1b" + (char)('0' + (key - Key.D0));
        }

        return null;
    }

    private static string Cursor(char final, int mod, bool applicationMode)
    {
        if (mod > 1)
            return $"\x1b[1;{mod}{final}";
        return applicationMode ? $"\x1bO{final}" : $"\x1b[{final}";
    }

    private static string Ss3(char final, int mod) =>
        mod > 1 ? $"\x1b[1;{mod}{final}" : $"\x1bO{final}";

    private static string Tilde(int code, int mod) =>
        mod > 1 ? $"\x1b[{code};{mod}~" : $"\x1b[{code}~";
}
