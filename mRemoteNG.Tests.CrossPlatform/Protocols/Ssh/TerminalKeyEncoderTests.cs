using Avalonia.Input;
using FluentAssertions;
using mRemoteNG.Protocols.Ssh.Terminal;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Ssh;

public sealed class TerminalKeyEncoderTests
{
    [Theory]
    [InlineData(Key.Up, KeyModifiers.None, false, "\x1b[A")]
    [InlineData(Key.Up, KeyModifiers.None, true, "\x1bOA")]
    [InlineData(Key.Left, KeyModifiers.Control, false, "\x1b[1;5D")]
    [InlineData(Key.Home, KeyModifiers.None, true, "\x1bOH")]
    [InlineData(Key.Delete, KeyModifiers.None, false, "\x1b[3~")]
    [InlineData(Key.PageUp, KeyModifiers.Shift, false, "\x1b[5;2~")]
    [InlineData(Key.F1, KeyModifiers.None, false, "\x1bOP")]
    [InlineData(Key.F5, KeyModifiers.None, false, "\x1b[15~")]
    [InlineData(Key.F12, KeyModifiers.Alt, false, "\x1b[24;3~")]
    [InlineData(Key.Enter, KeyModifiers.None, false, "\r")]
    [InlineData(Key.Back, KeyModifiers.None, false, "\x7f")]
    [InlineData(Key.Tab, KeyModifiers.Shift, false, "\x1b[Z")]
    [InlineData(Key.C, KeyModifiers.Control, false, "\x03")]
    [InlineData(Key.Z, KeyModifiers.Control, false, "\x1a")]
    [InlineData(Key.Space, KeyModifiers.Control, false, "\0")]
    [InlineData(Key.OemOpenBrackets, KeyModifiers.Control, false, "\x1b")]
    [InlineData(Key.B, KeyModifiers.Alt, false, "\u001bb")]
    [InlineData(Key.B, KeyModifiers.Alt | KeyModifiers.Shift, false, "\x1b" + "B")]
    public void Encode_ProducesXtermSequences(Key key, KeyModifiers modifiers, bool applicationCursor, string expected) =>
        TerminalKeyEncoder.Encode(key, modifiers, applicationCursor).Should().Be(expected);

    [Theory]
    [InlineData(Key.A, KeyModifiers.None)]
    [InlineData(Key.A, KeyModifiers.Shift)]
    [InlineData(Key.Q, KeyModifiers.Control | KeyModifiers.Alt)] // AltGr
    public void Encode_LeavesPrintableKeysToTextInput(Key key, KeyModifiers modifiers) =>
        TerminalKeyEncoder.Encode(key, modifiers, false).Should().BeNull();
}
