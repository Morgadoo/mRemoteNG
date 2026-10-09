using FluentAssertions;
using mRemoteNG.Protocols.Ssh.Terminal;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Ssh;

public sealed class TerminalScreenTests
{
    private const string Esc = "\x1b";

    private static TerminalScreen Screen(int cols = 20, int rows = 5, string input = "")
    {
        var screen = new TerminalScreen(cols, rows, scrollbackLimit: 100);
        screen.Feed(input);
        return screen;
    }

    // ── Text, wrapping, scrolling ──────────────────────────────────────────

    [Fact]
    public void PlainText_IsWrittenWithCrLf()
    {
        var s = Screen(input: "hello\r\nworld");

        s.GetLineText(0).Should().Be("hello");
        s.GetLineText(1).Should().Be("world");
        (s.CursorRow, s.CursorColumn).Should().Be((1, 5));
    }

    [Fact]
    public void LastColumn_WrapsOnlyWhenTheNextCharacterArrives()
    {
        var s = Screen(cols: 5, input: "abcde");
        (s.CursorRow, s.CursorColumn).Should().Be((0, 4));

        s.Feed("f");

        s.GetLineText(0).Should().Be("abcde");
        s.GetLineText(1).Should().Be("f");
    }

    [Fact]
    public void AutoWrapOff_OverwritesLastColumn()
    {
        var s = Screen(cols: 5, input: $"{Esc}[?7labcdefg");

        s.GetLineText(0).Should().Be("abcdg");
        s.CursorRow.Should().Be(0);
    }

    [Fact]
    public void LineFeedAtBottom_ScrollsIntoScrollback()
    {
        var s = Screen(rows: 3, input: "1\r\n2\r\n3\r\n4");

        s.GetScreenText().Should().Be("2\n3\n4");
        s.Scrollback.Should().ContainSingle();
        TerminalScreen.RowToString(s.Scrollback[0]).Should().Be("1");
    }

    [Fact]
    public void BackspaceAndTab_MoveTheCursor()
    {
        var s = Screen(input: "ab\bX\tY");

        s.GetLineText(0).Should().Be("aX      Y");
    }

    [Fact]
    public void Utf8SurrogatePairsSplitAcrossWrites_AreCombined()
    {
        var s = Screen();
        s.Feed("\ud83d");
        s.Feed("\ude00!");

        s.GetLineText(0).Should().Be("😀!");
        s.CursorColumn.Should().Be(2);
    }

    // ── SGR ────────────────────────────────────────────────────────────────

    [Fact]
    public void Sgr_SixteenColoursAndStyles()
    {
        var s = Screen(input: $"{Esc}[1;4;31;42mA{Esc}[0mB{Esc}[7;95;103mC");

        var a = s.GetCell(0, 0).Attributes;
        a.Foreground.Should().Be(TerminalColor.Indexed(1));
        a.Background.Should().Be(TerminalColor.Indexed(2));
        a.Style.Should().Be(TerminalStyle.Bold | TerminalStyle.Underline);

        s.GetCell(0, 1).Attributes.Should().Be(TerminalAttributes.Default);

        var c = s.GetCell(0, 2).Attributes;
        c.Foreground.Should().Be(TerminalColor.Indexed(13));
        c.Background.Should().Be(TerminalColor.Indexed(11));
        c.Has(TerminalStyle.Inverse).Should().BeTrue();
    }

    [Fact]
    public void Sgr_256ColourAndTruecolour_SemicolonForm()
    {
        var s = Screen(input: $"{Esc}[38;5;208;48;2;10;20;30mX");

        var a = s.GetCell(0, 0).Attributes;
        a.Foreground.Should().Be(TerminalColor.Indexed(208));
        a.Background.Should().Be(TerminalColor.Rgb(10, 20, 30));
    }

    [Fact]
    public void Sgr_ColonForms()
    {
        var s = Screen(input: $"{Esc}[38:2::1:2:3;48:5:17;4:0mX{Esc}[38:2:4:5:6mY");

        var x = s.GetCell(0, 0).Attributes;
        x.Foreground.Should().Be(TerminalColor.Rgb(1, 2, 3));
        x.Background.Should().Be(TerminalColor.Indexed(17));
        x.Has(TerminalStyle.Underline).Should().BeFalse();
        s.GetCell(0, 1).Attributes.Foreground.Should().Be(TerminalColor.Rgb(4, 5, 6));
    }

    [Fact]
    public void Sgr_IndividualResets()
    {
        var s = Screen(input: $"{Esc}[1;3;4;7;9;31;41m{Esc}[22;23;24;27;29;39;49mX");

        s.GetCell(0, 0).Attributes.Should().Be(TerminalAttributes.Default);
    }

    [Fact]
    public void Sgr_EmptyParameterResets()
    {
        var s = Screen(input: $"{Esc}[1;32m{Esc}[mX");

        s.GetCell(0, 0).Attributes.Should().Be(TerminalAttributes.Default);
    }

    [Fact]
    public void Palette_BoldBrightensFirstEightColours_AndInverseSwaps()
    {
        TerminalPalette.FromIndex(196).Should().Be(0xFF0000u);
        TerminalPalette.FromIndex(232).Should().Be(0x080808u);

        var bold = new TerminalAttributes(TerminalColor.Indexed(1), TerminalColor.Default, TerminalStyle.Bold);
        TerminalPalette.ResolveCell(bold).Foreground.Should().Be(TerminalPalette.FromIndex(9));

        var inverse = new TerminalAttributes(TerminalColor.Rgb(1, 2, 3), TerminalColor.Default, TerminalStyle.Inverse);
        TerminalPalette.ResolveCell(inverse).Should().Be((TerminalPalette.DefaultBackground, 0x010203u));
    }

    // ── Cursor movement and erasing ────────────────────────────────────────

    [Fact]
    public void CursorPosition_IsOneBasedAndClamped()
    {
        var s = Screen(input: $"{Esc}[3;5HX{Esc}[99;99HY{Esc}[HZ");

        s.GetCell(2, 4).Rune.ToString().Should().Be("X");
        s.GetCell(4, 19).Rune.ToString().Should().Be("Y");
        s.GetCell(0, 0).Rune.ToString().Should().Be("Z");
    }

    [Fact]
    public void RelativeMovement_CuuCudCufCubChaVpa()
    {
        var s = Screen(input: $"{Esc}[3;3H{Esc}[2A{Esc}[3C{Esc}[1B{Esc}[2D");
        (s.CursorRow, s.CursorColumn).Should().Be((1, 3));

        s.Feed($"{Esc}[10G{Esc}[4d");
        (s.CursorRow, s.CursorColumn).Should().Be((3, 9));
    }

    [Fact]
    public void EraseInLine_Modes()
    {
        var s = Screen(input: "abcdef\r\nabcdef\r\nabcdef");
        s.Feed($"{Esc}[1;3H{Esc}[K{Esc}[2;3H{Esc}[1K{Esc}[3;3H{Esc}[2K");

        s.GetLineText(0).Should().Be("ab");
        s.GetLineText(1).Should().Be("   def");
        s.GetLineText(2).Should().Be("");
    }

    [Fact]
    public void EraseInDisplay_Modes()
    {
        var s = Screen(rows: 3, input: "aaa\r\nbbb\r\nccc");
        s.Feed($"{Esc}[2;2H{Esc}[J");
        s.GetScreenText().Should().Be("aaa\nb\n");

        s.Feed($"{Esc}[1;2H{Esc}[1J");
        s.GetScreenText().Should().Be("  a\nb\n");

        s.Feed($"{Esc}[2J");
        s.GetScreenText().Should().Be("\n\n");
    }

    [Fact]
    public void Erase_UsesCurrentBackgroundColour()
    {
        var s = Screen(input: $"{Esc}[44m{Esc}[2J");

        s.GetCell(3, 3).Attributes.Background.Should().Be(TerminalColor.Indexed(4));
    }

    [Fact]
    public void InsertAndDeleteCharacters()
    {
        var s = Screen(input: "abcdef\r");
        s.Feed($"{Esc}[2C{Esc}[2@");
        s.GetLineText(0).Should().Be("ab  cdef");

        s.Feed($"{Esc}[3P");
        s.GetLineText(0).Should().Be("abdef");

        s.Feed($"{Esc}[2X");
        s.GetLineText(0).Should().Be("ab  f");
    }

    [Fact]
    public void InsertAndDeleteLines()
    {
        var s = Screen(rows: 4, input: "1\r\n2\r\n3\r\n4");
        s.Feed($"{Esc}[2H{Esc}[L");
        s.GetScreenText().Should().Be("1\n\n2\n3");

        s.Feed($"{Esc}[2M");
        s.GetScreenText().Should().Be("1\n3\n\n");
    }

    // ── Scroll regions ─────────────────────────────────────────────────────

    [Fact]
    public void ScrollRegion_LineFeedScrollsOnlyTheRegion()
    {
        var s = Screen(rows: 5, input: "top\r\n1\r\n2\r\n3\r\nbottom");
        s.Feed($"{Esc}[2;4r");
        (s.ScrollTop, s.ScrollBottom).Should().Be((1, 3));
        (s.CursorRow, s.CursorColumn).Should().Be((0, 0));

        s.Feed($"{Esc}[4;1H\nnew");

        s.GetScreenText().Should().Be("top\n2\n3\nnew\nbottom");
        s.Scrollback.Should().BeEmpty();
    }

    [Fact]
    public void ScrollRegion_ReverseIndexAtTopScrollsDown()
    {
        var s = Screen(rows: 4, input: "a\r\nb\r\nc\r\nd");
        s.Feed($"{Esc}[2;3r{Esc}[2;1H{Esc}M");

        s.GetScreenText().Should().Be("a\n\nb\nd");
    }

    [Fact]
    public void ScrollUpAndDown_Sequences()
    {
        var s = Screen(rows: 3, input: "a\r\nb\r\nc");
        s.Feed($"{Esc}[S");
        s.GetScreenText().Should().Be("b\nc\n");

        s.Feed($"{Esc}[2T");
        s.GetScreenText().Should().Be("\n\nb");
    }

    [Fact]
    public void OriginMode_PositionsRelativeToRegion()
    {
        var s = Screen(rows: 6, input: $"{Esc}[3;5r{Esc}[?6h{Esc}[1;1HX{Esc}[9;1HY");

        s.GetCell(2, 0).Rune.ToString().Should().Be("X");
        s.GetCell(4, 0).Rune.ToString().Should().Be("Y");
    }

    // ── Modes, alternate screen, save/restore ──────────────────────────────

    [Fact]
    public void AlternateScreen1049_PreservesMainScreenAndCursor()
    {
        var s = Screen(input: "main text");
        s.Feed($"{Esc}[?1049h");
        s.IsAlternateScreen.Should().BeTrue();
        s.GetLineText(0).Should().Be("");

        s.Feed($"{Esc}[3;3Hfull screen app");
        s.Feed($"{Esc}[?1049l");

        s.IsAlternateScreen.Should().BeFalse();
        s.GetLineText(0).Should().Be("main text");
        s.GetLineText(2).Should().Be("");
        (s.CursorRow, s.CursorColumn).Should().Be((0, 9));
    }

    [Fact]
    public void AlternateScreen_DoesNotFillScrollback()
    {
        var s = Screen(rows: 3, input: $"{Esc}[?1049h1\r\n2\r\n3\r\n4\r\n5");

        s.Scrollback.Should().BeEmpty();
    }

    [Fact]
    public void SaveAndRestoreCursor_IncludesAttributes()
    {
        var s = Screen(input: $"{Esc}[2;3H{Esc}[31m{Esc}7{Esc}[0m{Esc}[5;5H{Esc}8X");

        s.GetCell(1, 2).Rune.ToString().Should().Be("X");
        s.GetCell(1, 2).Attributes.Foreground.Should().Be(TerminalColor.Indexed(1));

        s.Feed($"{Esc}[4;4H{Esc}[s{Esc}[1;1H{Esc}[uY");
        s.GetCell(3, 3).Rune.ToString().Should().Be("Y");
    }

    [Fact]
    public void PrivateModes_CursorVisibilityKeysAndPaste()
    {
        var s = Screen(input: $"{Esc}[?25l{Esc}[?1h{Esc}[?2004h");

        s.CursorVisible.Should().BeFalse();
        s.ApplicationCursorKeys.Should().BeTrue();
        s.BracketedPaste.Should().BeTrue();

        s.Feed($"{Esc}[?25h{Esc}[?1;2004l");

        s.CursorVisible.Should().BeTrue();
        s.ApplicationCursorKeys.Should().BeFalse();
        s.BracketedPaste.Should().BeFalse();
    }

    [Fact]
    public void InsertMode_ShiftsText()
    {
        var s = Screen(input: $"abc\r{Esc}[4hX{Esc}[4lY");

        s.GetLineText(0).Should().Be("XYbc");
    }

    [Fact]
    public void DecLineDrawing_MapsToBoxCharacters()
    {
        var s = Screen(input: $"{Esc}(0lqk{Esc}(Bq");

        s.GetLineText(0).Should().Be("┌─┐q");
    }

    // ── Replies, OSC, robustness ───────────────────────────────────────────

    [Fact]
    public void DeviceStatusAndAttributes_ProduceReplies()
    {
        var s = Screen();
        var replies = new List<string>();
        s.Response += replies.Add;

        s.Feed($"{Esc}[3;7H{Esc}[6n{Esc}[5n{Esc}[c{Esc}[>c");

        replies.Should().Equal($"{Esc}[3;7R", $"{Esc}[0n", $"{Esc}[?1;2c", $"{Esc}[>0;10;0c");
    }

    [Fact]
    public void Osc_SetsTitle_AndIsNotPrinted()
    {
        var s = Screen(input: $"{Esc}]0;user@host: ~\aA{Esc}]2;second{Esc}\\B");

        s.Title.Should().Be("second");
        s.GetLineText(0).Should().Be("AB");
    }

    [Fact]
    public void UnknownSequencesAndDcs_AreIgnored()
    {
        var s = Screen(input: $"{Esc}[?1000h{Esc}[2 q{Esc}P+q544e{Esc}\\{Esc}=ok");

        s.GetLineText(0).Should().Be("ok");
    }

    [Fact]
    public void SequencesSplitAcrossWrites_AreParsed()
    {
        var s = Screen();
        s.Feed($"{Esc}");
        s.Feed("[3");
        s.Feed("1mR");

        s.GetCell(0, 0).Attributes.Foreground.Should().Be(TerminalColor.Indexed(1));
        s.GetLineText(0).Should().Be("R");
    }

    [Fact]
    public void Repeat_RepeatsLastCharacter()
    {
        var s = Screen(input: $"-{Esc}[4b");

        s.GetLineText(0).Should().Be("-----");
    }

    [Fact]
    public void Resize_KeepsCursorLineVisible_AndPushesTopIntoScrollback()
    {
        var s = Screen(cols: 10, rows: 4, input: "1\r\n2\r\n3\r\n4");

        s.Resize(5, 2);

        (s.Columns, s.Rows).Should().Be((5, 2));
        s.GetScreenText().Should().Be("3\n4");
        s.Scrollback.Select(TerminalScreen.RowToString).Should().Equal("1", "2");
        (s.CursorRow, s.CursorColumn).Should().Be((1, 1));

        s.Resize(8, 3);
        s.GetScreenText().Should().Be("3\n4\n");
        s.Feed("\r\n\r\nend");
        s.GetLineText(2).Should().Be("end");
    }

    [Fact]
    public void FullReset_ClearsScreenAndModes()
    {
        var s = Screen(input: $"text{Esc}[?25l{Esc}[31m{Esc}[2;3r{Esc}c");

        s.GetScreenText().Trim().Should().BeEmpty();
        s.CursorVisible.Should().BeTrue();
        s.CurrentAttributes.Should().Be(TerminalAttributes.Default);
        (s.ScrollTop, s.ScrollBottom).Should().Be((0, 4));
    }

    [Fact]
    public void HugeParameters_AreClamped()
    {
        var s = Screen(input: $"{Esc}[99999999999999999999;5H{Esc}[99999999LX");

        s.GetCell(4, 0).Rune.ToString().Should().Be("X");
    }
}
