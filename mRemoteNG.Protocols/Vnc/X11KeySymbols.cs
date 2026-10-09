using System.Text;
using Avalonia.Input;

namespace mRemoteNG.Protocols.Vnc;

/// <summary>Translates Avalonia key events into the X11 keysyms that RFB KeyEvent messages carry.</summary>
public static class X11KeySymbols
{
    public const uint BackSpace = 0xFF08;
    public const uint Tab = 0xFF09;
    public const uint Return = 0xFF0D;
    public const uint Escape = 0xFF1B;
    public const uint Delete = 0xFFFF;
    public const uint Home = 0xFF50;
    public const uint Left = 0xFF51;
    public const uint Up = 0xFF52;
    public const uint Right = 0xFF53;
    public const uint Down = 0xFF54;
    public const uint PageUp = 0xFF55;
    public const uint PageDown = 0xFF56;
    public const uint End = 0xFF57;
    public const uint Insert = 0xFF63;
    public const uint F1 = 0xFFBE;
    public const uint ShiftL = 0xFFE1;
    public const uint ShiftR = 0xFFE2;
    public const uint ControlL = 0xFFE3;
    public const uint ControlR = 0xFFE4;
    public const uint AltL = 0xFFE9;
    public const uint AltR = 0xFFEA;
    public const uint SuperL = 0xFFEB;
    public const uint SuperR = 0xFFEC;

    /// <summary>
    /// Returns the keysym for a key event, or null when the key has no sensible X11 equivalent.
    /// Named keys (navigation, function, modifier, keypad) map by <paramref name="key"/>; anything else uses the
    /// character the platform layout produced (<paramref name="keySymbol"/>, i.e. <c>KeyEventArgs.KeySymbol</c>),
    /// falling back to a US layout when that is missing or a control character (e.g. while Ctrl is held).
    /// </summary>
    public static uint? FromKey(Key key, string? keySymbol, KeyModifiers modifiers = KeyModifiers.None)
    {
        var named = NamedKey(key);
        if (named is not null)
            return named;

        if (!string.IsNullOrEmpty(keySymbol))
        {
            var rune = Rune.GetRuneAt(keySymbol, 0);
            if (rune.Value >= 0x20 && rune.Value != 0x7F)
                return FromCodePoint(rune.Value);
        }

        var shift = (modifiers & KeyModifiers.Shift) != 0;
        if (key is >= Key.A and <= Key.Z)
            return (uint)((shift ? 'A' : 'a') + (key - Key.A));
        if (key is >= Key.D0 and <= Key.D9)
            return shift ? ")!@#$%^&*("[key - Key.D0] : (uint)('0' + (key - Key.D0));

        // Send the shifted symbol while Shift is held: servers produce exactly the keysym they are given,
        // so sending '1' with Shift down would type "1", not "!".
        char? us = key switch
        {
            Key.OemSemicolon => shift ? ':' : ';',
            Key.OemPlus => shift ? '+' : '=',
            Key.OemComma => shift ? '<' : ',',
            Key.OemMinus => shift ? '_' : '-',
            Key.OemPeriod => shift ? '>' : '.',
            Key.OemQuestion => shift ? '?' : '/',
            Key.OemTilde => shift ? '~' : '`',
            Key.OemOpenBrackets => shift ? '{' : '[',
            Key.OemPipe or Key.OemBackslash => shift ? '|' : '\\',
            Key.OemCloseBrackets => shift ? '}' : ']',
            Key.OemQuotes => shift ? '"' : '\'',
            _ => null,
        };
        return us;
    }

    /// <summary>Keysym for a Unicode character: Latin-1 maps directly, everything else to 0x01000000 + code point.</summary>
    public static uint FromCodePoint(int codePoint) =>
        codePoint is >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF
            ? (uint)codePoint
            : 0x01000000u | (uint)codePoint;

    private static uint? NamedKey(Key key)
    {
        if (key is >= Key.F1 and <= Key.F24)
            return F1 + (uint)(key - Key.F1);
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return 0xFFB0 + (uint)(key - Key.NumPad0);

        return key switch
        {
            Key.Back => BackSpace,
            Key.Tab => Tab,
            Key.Enter => Return,
            Key.Escape => Escape,
            Key.Space => 0x20,
            Key.Delete => Delete,
            Key.Insert => Insert,
            Key.Home => Home,
            Key.End => End,
            Key.PageUp => PageUp,
            Key.PageDown => PageDown,
            Key.Left => Left,
            Key.Up => Up,
            Key.Right => Right,
            Key.Down => Down,
            Key.LeftShift => ShiftL,
            Key.RightShift => ShiftR,
            Key.LeftCtrl => ControlL,
            Key.RightCtrl => ControlR,
            Key.LeftAlt => AltL,
            Key.RightAlt => AltR,
            // The Windows / Command key is Super on X11 desktops (what TigerVNC and others send as well).
            Key.LWin => SuperL,
            Key.RWin => SuperR,
            Key.Apps => 0xFF67,      // Menu
            Key.CapsLock => 0xFFE5,
            Key.NumLock => 0xFF7F,
            Key.Scroll => 0xFF14,
            Key.Pause => 0xFF13,
            Key.PrintScreen => 0xFF61,
            Key.Multiply => 0xFFAA,  // KP_Multiply
            Key.Add => 0xFFAB,       // KP_Add
            Key.Separator => 0xFFAC, // KP_Separator
            Key.Subtract => 0xFFAD,  // KP_Subtract
            Key.Decimal => 0xFFAE,   // KP_Decimal
            Key.Divide => 0xFFAF,    // KP_Divide
            _ => null,
        };
    }
}
