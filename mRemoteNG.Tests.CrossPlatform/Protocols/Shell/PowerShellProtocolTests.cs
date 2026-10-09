using System.Text;
using FluentAssertions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Shell;
using Xunit;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Shell;

public sealed class PowerShellProtocolTests
{
    private static ConnectionParameters Params(string host, string user = "") => new()
    {
        Hostname = host,
        Port = 0,
        Username = user,
        Protocol = ProtocolType.PowerShell,
    };

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void BuildArguments_Local_OnTerminal_IsAPlainInteractiveShell(string host)
    {
        PowerShellProtocol.BuildArguments(Params(host), PowerShellHostMode.PseudoTerminal, 80)
            .Should().Equal("-NoLogo", "-NoProfile", "-Interactive");
    }

    [Fact]
    public void BuildArguments_Remote_EntersAPSSession()
    {
        PowerShellProtocol.BuildArguments(Params("server01", "CORP\\bob"), PowerShellHostMode.PseudoTerminal, 80)
            .Should().Equal("-NoLogo", "-NoProfile", "-NoExit", "-Command",
                "Enter-PSSession -ComputerName 'server01' -Credential 'CORP\\bob'");
    }

    [Fact]
    public void BuildArguments_Remote_QuotesValuesAsLiterals()
    {
        var args = PowerShellProtocol.BuildArguments(Params("h'; rm -r /; '", "o'brien"), PowerShellHostMode.PseudoTerminal, 80);

        args[^1].Should().Be("Enter-PSSession -ComputerName 'h''; rm -r /; ''' -Credential 'o''brien'");
    }

    [Fact]
    public void BuildArguments_Remote_WithoutUser_OmitsCredential()
    {
        PowerShellProtocol.BuildArguments(Params("server01"), PowerShellHostMode.PseudoTerminal, 80)[^1]
            .Should().Be("Enter-PSSession -ComputerName 'server01'");
    }

    [Fact]
    public void BuildArguments_OnPipes_RendersOutputAtTheViewWidth()
    {
        var args = PowerShellProtocol.BuildArguments(Params("localhost"), PowerShellHostMode.Pipes, 100);

        args.Take(4).Should().Equal("-NoLogo", "-NoProfile", "-NoExit", "-Command");
        args[^1].Should().Contain("Out-String -Stream -Width 99");
    }

    [Fact]
    public void BuildArguments_RemoteOnPipes_SetsUpOutputBeforeEnteringTheSession()
    {
        var command = PowerShellProtocol.BuildArguments(Params("server01"), PowerShellHostMode.Pipes, 80)[^1];

        command.Should().StartWith("function global:Out-Default").And.EndWith("; Enter-PSSession -ComputerName 'server01'");
    }

    [Theory]
    [InlineData("dir\r", "dir\n")]
    [InlineData("dir\r\n", "dir\n")]
    [InlineData("a\rb\r", "a\nb\n")]
    [InlineData("a\n", "a\n")]
    [InlineData("plain", "plain")]
    public void TranslateEnter_MapsTheTerminalsEnterToNewline(string input, string expected)
    {
        bool lastWasCr = false;

        var output = PowerShellProtocol.TranslateEnter(Encoding.ASCII.GetBytes(input), ref lastWasCr);

        Encoding.ASCII.GetString(output).Should().Be(expected);
    }

    [Fact]
    public void TranslateEnter_CrLfSplitAcrossWrites_GivesOneNewline()
    {
        bool lastWasCr = false;

        var first = PowerShellProtocol.TranslateEnter("ls\r"u8.ToArray(), ref lastWasCr);
        var second = PowerShellProtocol.TranslateEnter("\npwd\r"u8.ToArray(), ref lastWasCr);

        Encoding.ASCII.GetString([.. first, .. second]).Should().Be("ls\npwd\n");
    }
}
