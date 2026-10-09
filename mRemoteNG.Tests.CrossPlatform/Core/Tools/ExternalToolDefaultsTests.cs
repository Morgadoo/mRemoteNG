using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Tools;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Tools;

public sealed class ExternalToolDefaultsTests
{
    private static List<string> Argv(ExternalTool tool, ConnectionInfo connection)
    {
        var parser = new ExternalToolArgumentParser(ExternalToolVariables.FromConnection(connection), ArgumentEscapingStyle.Posix, _ => null);
        return [parser.ParseArguments(tool.FileName), .. CommandLineTokenizer.Split(parser.ParseArguments(tool.Arguments))];
    }

    [Fact]
    public void Linux_UsesTheFirstInstalledTerminal()
    {
        var tools = ExternalToolDefaults.Create(ExternalToolPlatform.Linux, p => p is "xterm" or "traceroute");

        tools.Select(t => t.DisplayName).Should().Equal("Ping", "Traceroute", "Copy SSH key (ssh-copy-id)", "Terminal");
        tools.Take(3).Should().OnlyContain(t => t.FileName == "xterm");
        Argv(tools[0], new ConnectionInfo { Hostname = "srv01" }).Should().Equal("xterm", "-e", "ping", "srv01");
    }

    [Fact]
    public void Linux_ConnectionValuesNeverBecomeShellCode()
    {
        var tools = ExternalToolDefaults.Create(ExternalToolPlatform.Linux, p => p is "gnome-terminal" or "traceroute");
        var hostile = new ConnectionInfo { Hostname = "h; touch /tmp/pwned \"$(id)\"", Username = "", Port = 2222 };

        var traceroute = Argv(tools[1], hostile);
        traceroute.Take(4).Should().Equal("gnome-terminal", "--", "sh", "-c");
        traceroute[4].Should().StartWith("traceroute \"$1\"").And.NotContain("pwned");
        traceroute.Skip(5).Should().Equal("sh", hostile.Hostname);

        var copyId = Argv(tools[2], hostile);
        copyId.Skip(5).Should().Equal("sh", "", hostile.Hostname, "2222");
        copyId[4].Should().Contain("ssh-copy-id -p \"$p\" \"$t\"");
    }

    [Fact]
    public void Linux_IntegratedTerminal_WhenXtermIsInstalled()
    {
        var withXterm = ExternalToolDefaults.Create(ExternalToolPlatform.Linux, p => p is "konsole" or "xterm");
        var withoutXterm = ExternalToolDefaults.Create(ExternalToolPlatform.Linux, p => p is "konsole" or "tracepath");

        withXterm.Last().Should().BeEquivalentTo(new { FileName = "xterm", TryIntegrate = true, WaitForExit = false });
        withoutXterm.Last().Should().BeEquivalentTo(new { FileName = "konsole", TryIntegrate = false });
        withoutXterm[1].Arguments.Should().Contain("tracepath \"$1\"", "traceroute is not installed");
    }

    [Fact]
    public void Linux_WithoutAnyTerminal_HasNoTools() =>
        ExternalToolDefaults.Create(ExternalToolPlatform.Linux, _ => false).Should().BeEmpty();

    [Fact]
    public void Windows_UsesCmdLikeTheLegacyExamples()
    {
        var tools = ExternalToolDefaults.Create(ExternalToolPlatform.Windows, _ => false);

        tools[0].Should().BeEquivalentTo(new { DisplayName = "Ping", FileName = "cmd", Arguments = "/K ping -t %HOSTNAME%" });
        new ExternalToolArgumentParser(ExternalToolVariables.FromConnection(new ConnectionInfo { Hostname = "srv" }), ArgumentEscapingStyle.WindowsShell)
            .ParseArguments(tools[1].Arguments).Should().Be("/K tracert srv");
    }

    [Fact]
    public void Mac_PassesValuesAsOsascriptArguments()
    {
        var tools = ExternalToolDefaults.Create(ExternalToolPlatform.MacOS, _ => false);

        var argv = Argv(tools[0], new ConnectionInfo { Hostname = "a b'c" });

        argv.Should().Equal("osascript", "-e", "on run argv", "-e",
            "tell application \"Terminal\" to do script \"ping \" & quoted form of item 1 of argv", "-e", "end run", "a b'c");
    }

    [Fact]
    public void DefaultTools_AreUniqueByName()
    {
        foreach (var platform in Enum.GetValues<ExternalToolPlatform>())
            ExternalToolDefaults.Create(platform, _ => true).Select(t => t.DisplayName).Should().OnlyHaveUniqueItems();
    }
}
