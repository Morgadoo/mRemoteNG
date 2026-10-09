using FluentAssertions;
using mRemoteNG.Core.Config.Putty;
using mRemoteNG.Protocols.Ssh;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Ssh;

public class SshOptionsParserTests
{
    [Fact]
    public void Parse_Empty_ReturnsDefaults()
    {
        var options = SshOptionsParser.Parse("  ");

        options.Should().BeEquivalentTo(new SshCommandLineOptions());
    }

    [Fact]
    public void Parse_Forwardings_SeparateAndAttached_WithAndWithoutBindAddress()
    {
        var options = SshOptionsParser.Parse(
            "-L 8080:intranet:80 -L0.0.0.0:8443:[fe80::1]:443 -R 2222:localhost:22 -R [::1]:9000:db:5432 -D 1080 -D 127.0.0.2:1081 -D1082");

        options.Forwards.Should().Equal(
            new PortForwardSpec(PortForwardKind.Local, null, 8080, "intranet", 80),
            new PortForwardSpec(PortForwardKind.Local, "0.0.0.0", 8443, "fe80::1", 443),
            new PortForwardSpec(PortForwardKind.Remote, null, 2222, "localhost", 22),
            new PortForwardSpec(PortForwardKind.Remote, "::1", 9000, "db", 5432),
            new PortForwardSpec(PortForwardKind.Dynamic, null, 1080),
            new PortForwardSpec(PortForwardKind.Dynamic, "127.0.0.2", 1081),
            new PortForwardSpec(PortForwardKind.Dynamic, null, 1082));
        options.Notices.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ConnectionSwitches()
    {
        var options = SshOptionsParser.Parse("-C -N -P 2200 -l admin -pw \"se cret\" -i '/keys/my key.ppk' -load \"Jump host\"");

        options.Compression.Should().BeTrue();
        options.NoShell.Should().BeTrue();
        options.Port.Should().Be(2200);
        options.Username.Should().Be("admin");
        options.Password.Should().Be("se cret");
        options.IdentityFile.Should().Be("/keys/my key.ppk");
        options.LoadSession.Should().Be("Jump host");
        options.Notices.Should().BeEmpty();
    }

    [Fact]
    public void Parse_PasswordFile_ReadsFirstLine()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "from-file\nignored\n");
            SshOptionsParser.Parse($"-pwfile \"{file}\"").Password.Should().Be("from-file");
            SshOptionsParser.Parse("-pwfile /does/not/exist").Notices.Should().ContainSingle()
                .Which.Should().Contain("could not be read");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Parse_UnsupportedOptions_AreReportedNotDropped()
    {
        var options = SshOptionsParser.Parse("-A -X -m cmds.txt -T -bogus");

        options.Notices.Should().HaveCount(5);
        options.Notices[0].Should().StartWith("Agent forwarding (-A) is not supported");
        options.Notices[1].Should().StartWith("X11 forwarding (-X) is not supported");
        options.Notices[2].Should().Contain("-m cmds.txt").And.Contain("not supported");
        options.Notices[3].Should().Contain("-T");
        options.Notices[4].Should().Be("Unknown SSH option \"-bogus\" was ignored.");
    }

    [Fact]
    public void Parse_DefaultRestatingSwitches_AreSilent()
    {
        SshOptionsParser.Parse("-2 -ssh -a -x -t -noagent -batch -v").Notices.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MalformedValues_AreReported()
    {
        var options = SshOptionsParser.Parse("-L 8080:intranet -D notaport -P 99999 -l");

        options.Forwards.Should().BeEmpty();
        options.Port.Should().BeNull();
        options.Notices.Should().HaveCount(4);
        options.Notices[0].Should().Contain("Invalid local forwarding \"8080:intranet\"");
        options.Notices[1].Should().Contain("Invalid port \"notaport\"");
        options.Notices[2].Should().Contain("Invalid port \"99999\"");
        options.Notices[3].Should().Be("SSH option -l needs an argument and was ignored.");
    }

    [Theory]
    [InlineData("a b", new[] { "a", "b" })]
    [InlineData("  -i \"C:\\Users\\me\\key.ppk\"  ", new[] { "-i", "C:\\Users\\me\\key.ppk" })]
    [InlineData("-pw 'it''s' -l \"a\\\"b\"", new[] { "-pw", "its", "-l", "a\"b" })]
    [InlineData("-pw \"\"", new[] { "-pw", "" })]
    public void Tokenize_HandlesQuotes(string commandLine, string[] expected)
    {
        SshOptionsParser.Tokenize(commandLine).Should().Equal(expected);
    }

    [Fact]
    public void PortForwardSpec_SerializeRoundTrips()
    {
        PortForwardSpec[] forwards =
        [
            new(PortForwardKind.Local, null, 8080, "intranet", 80),
            new(PortForwardKind.Remote, "0.0.0.0", 2222, "::1", 22),
            new(PortForwardKind.Dynamic, "127.0.0.1", 1080),
        ];

        PortForwardSpec.DeserializeAll(PortForwardSpec.SerializeAll(forwards)).Should().Equal(forwards);
        PortForwardSpec.Deserialize("X|a|1|b|2").Should().BeNull();
        forwards[0].Describe().Should().Be("local 127.0.0.1:8080 → intranet:80");
        forwards[1].Describe().Should().Be("remote 0.0.0.0:2222 (on the server) → [::1]:22");
    }
}
