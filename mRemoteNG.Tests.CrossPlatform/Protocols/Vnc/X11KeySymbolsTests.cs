using Avalonia.Input;
using FluentAssertions;
using mRemoteNG.Protocols.Vnc;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

public class X11KeySymbolsTests
{
    [Theory]
    [InlineData(Key.Enter, 0xFF0Du)]
    [InlineData(Key.Back, 0xFF08u)]
    [InlineData(Key.Tab, 0xFF09u)]
    [InlineData(Key.Escape, 0xFF1Bu)]
    [InlineData(Key.Left, 0xFF51u)]
    [InlineData(Key.Up, 0xFF52u)]
    [InlineData(Key.Right, 0xFF53u)]
    [InlineData(Key.Down, 0xFF54u)]
    [InlineData(Key.Home, 0xFF50u)]
    [InlineData(Key.End, 0xFF57u)]
    [InlineData(Key.PageUp, 0xFF55u)]
    [InlineData(Key.PageDown, 0xFF56u)]
    [InlineData(Key.Insert, 0xFF63u)]
    [InlineData(Key.Delete, 0xFFFFu)]
    [InlineData(Key.F1, 0xFFBEu)]
    [InlineData(Key.F5, 0xFFC2u)]
    [InlineData(Key.F12, 0xFFC9u)]
    [InlineData(Key.LeftShift, 0xFFE1u)]
    [InlineData(Key.RightShift, 0xFFE2u)]
    [InlineData(Key.LeftCtrl, 0xFFE3u)]
    [InlineData(Key.RightCtrl, 0xFFE4u)]
    [InlineData(Key.LeftAlt, 0xFFE9u)]
    [InlineData(Key.RightAlt, 0xFFEAu)]
    [InlineData(Key.LWin, 0xFFEBu)]
    [InlineData(Key.RWin, 0xFFECu)]
    [InlineData(Key.NumPad0, 0xFFB0u)]
    [InlineData(Key.NumPad9, 0xFFB9u)]
    [InlineData(Key.Space, 0x20u)]
    public void NamedKeys_MapToKeysyms(Key key, uint expected)
    {
        // Named keys win over whatever text the platform attached.
        X11KeySymbols.FromKey(key, keySymbol: "\r").Should().Be(expected);
    }

    [Theory]
    [InlineData(Key.A, "a", 0x61u)]
    [InlineData(Key.A, "A", 0x41u)]
    [InlineData(Key.D1, "!", 0x21u)]
    [InlineData(Key.OemPeriod, ".", 0x2Eu)]
    [InlineData(Key.E, "é", 0xE9u)]
    [InlineData(Key.E, "€", 0x010020ACu)]
    public void PrintableKeys_UseTheProducedCharacter(Key key, string symbol, uint expected)
    {
        X11KeySymbols.FromKey(key, symbol).Should().Be(expected);
    }

    [Theory]
    [InlineData(Key.C, "\u0003", KeyModifiers.Control, 0x63u)]
    [InlineData(Key.C, null, KeyModifiers.Control | KeyModifiers.Shift, 0x43u)]
    [InlineData(Key.D7, null, KeyModifiers.None, 0x37u)]
    [InlineData(Key.D1, "", KeyModifiers.Shift, 0x21u)]     // '!' (seen on X11 when KeySymbol is empty)
    [InlineData(Key.D0, "", KeyModifiers.Shift, 0x29u)]     // ')'
    [InlineData(Key.OemMinus, "", KeyModifiers.Control, 0x2Du)]
    [InlineData(Key.OemSemicolon, null, KeyModifiers.Shift, 0x3Au)]
    public void ControlCharactersOrMissingSymbol_FallBackToKey(Key key, string? symbol, KeyModifiers modifiers, uint expected)
    {
        X11KeySymbols.FromKey(key, symbol, modifiers).Should().Be(expected);
    }

    [Fact]
    public void UnmappableKey_ReturnsNull()
    {
        X11KeySymbols.FromKey(Key.VolumeUp, null).Should().BeNull();
    }
}
