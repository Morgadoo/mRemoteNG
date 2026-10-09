using FluentAssertions;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Config.Putty;
using mRemoteNG.Core.Connection;
using mRemoteNG.Platform;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Ssh;

public sealed class SshSettingsPreparationStepTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "putty-step-" + Guid.NewGuid().ToString("N"));
    private readonly ConnectionPreparer _preparer;

    public SshSettingsPreparationStepTests()
    {
        Directory.CreateDirectory(_directory);
        _preparer = new ConnectionPreparer([new SshSettingsPreparationStep(new PuttySessionCatalog(new PuttySessionFilesProvider(_directory)))]);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void WriteSession(string name, params string[] lines) =>
        File.WriteAllLines(Path.Combine(_directory, Uri.EscapeDataString(name)), lines);

    private async Task<ConnectionParameters> PrepareAsync(ConnectionInfo connection)
    {
        await using var prepared = await _preparer.PrepareAsync(connection);
        return prepared.Parameters;
    }

    private static ConnectionInfo Ssh(string host = "", string user = "", string session = "Default Settings", string options = "") => new()
    {
        Name = "target",
        Protocol = CoreProtocol.SSH2,
        Port = 22,
        Hostname = host,
        Username = user,
        PuttySession = session,
        SSHOptions = options,
    };

    [Fact]
    public async Task NoSessionNoOptions_LeavesParametersAlone()
    {
        var parameters = await PrepareAsync(Ssh("host", "me"));

        (parameters.Hostname, parameters.Port, parameters.Username).Should().Be(("host", 22, "me"));
        parameters.Extras.Keys.Should().NotContain(k => k.StartsWith("ssh."));
    }

    [Fact]
    public async Task SavedSession_FillsGaps_AndCarriesSshSettings()
    {
        WriteSession("jump",
            "HostName=jump.example.com", "PortNumber=2200", "UserName=alice", "Protocol=ssh",
            "PublicKeyFile=/keys/jump.ppk", "Compression=1", "X11Forward=1",
            "PortForwardings=L8080=intranet:80,D1080=",
            "ProxyMethod=3", "ProxyHost=proxy", "ProxyPort=3128", "ProxyUsername=pu", "ProxyPassword=pp",
            "RemoteCommand=tmux attach");

        var parameters = await PrepareAsync(Ssh(session: "jump"));

        (parameters.Hostname, parameters.Port, parameters.Username, parameters.PrivateKeyPath)
            .Should().Be(("jump.example.com", 2200, "alice", "/keys/jump.ppk"));
        parameters.Extras[SshExtras.Compression].Should().Be("true");
        SshExtras.GetForwards(parameters).Should().Equal(
            new PortForwardSpec(PortForwardKind.Local, null, 8080, "intranet", 80),
            new PortForwardSpec(PortForwardKind.Dynamic, null, 1080));
        parameters.Extras[SshExtras.ProxyType].Should().Be("http");
        parameters.Extras[SshExtras.ProxyHost].Should().Be("proxy");
        parameters.Extras[SshExtras.ProxyPort].Should().Be("3128");
        parameters.Extras[SshExtras.ProxyUsername].Should().Be("pu");
        parameters.Extras[SshExtras.ProxyPassword].Should().Be("pp");
        parameters.Extras[ConnectionParametersFactory.Keys.OpeningCommand].Should().Be("tmux attach");
        SshExtras.GetNotices(parameters).Should().HaveCount(2)
            .And.Contain(n => n.StartsWith("X11 forwarding (PuTTY session \"jump\")"))
            .And.Contain(n => n.Contains("remote command"));
    }

    [Fact]
    public async Task ConnectionValues_WinOverTheSavedSession()
    {
        WriteSession("jump", "HostName=jump.example.com", "PortNumber=2200", "UserName=alice", "Protocol=ssh");
        var connection = Ssh("own.example.com", "bob", "jump");
        connection.Port = 2022;

        var parameters = await PrepareAsync(connection);

        (parameters.Hostname, parameters.Port, parameters.Username).Should().Be(("own.example.com", 2022, "bob"));
    }

    [Fact]
    public async Task SessionPortOfAnotherProtocol_IsNotApplied()
    {
        WriteSession("router", "HostName=router", "PortNumber=2323", "Protocol=telnet");

        var parameters = await PrepareAsync(Ssh(session: "router"));

        (parameters.Hostname, parameters.Port).Should().Be(("router", 22));
    }

    [Fact]
    public async Task SshOptions_OverrideEverything_AndLoadAnotherSession()
    {
        WriteSession("jump", "HostName=jump.example.com", "PortNumber=2200", "UserName=alice", "Protocol=ssh", "PortForwardings=L8080=intranet:80");
        WriteSession("extra", "PortForwardings=L8080=other:80,R9000=localhost:9000", "Compression=0");

        var parameters = await PrepareAsync(Ssh(session: "jump",
            options: "-P 2222 -l root -pw pw -i /keys/id -C -N -load extra -L 8081:web:80 -A"));

        (parameters.Hostname, parameters.Port, parameters.Username, parameters.Password, parameters.PrivateKeyPath)
            .Should().Be(("jump.example.com", 2222, "root", "pw", "/keys/id"));
        parameters.Extras[SshExtras.Compression].Should().Be("true");
        parameters.Extras[SshExtras.NoShell].Should().Be("true");
        SshExtras.GetForwards(parameters).Should().Equal(
            new PortForwardSpec(PortForwardKind.Local, null, 8080, "intranet", 80), // first listener on 8080 wins
            new PortForwardSpec(PortForwardKind.Remote, null, 9000, "localhost", 9000),
            new PortForwardSpec(PortForwardKind.Local, null, 8081, "web", 80));
        SshExtras.GetNotices(parameters).Should().ContainSingle().Which.Should().StartWith("Agent forwarding (-A)");
    }

    [Fact]
    public async Task MissingSession_IsReported()
    {
        var parameters = await PrepareAsync(Ssh("host", session: "gone"));

        SshExtras.GetNotices(parameters).Should().ContainSingle()
            .Which.Should().Be("PuTTY saved session \"gone\" was not found; its settings were not applied.");
    }

    [Fact]
    public async Task ProxyExcludedOrUnsupported_IsNotUsed()
    {
        WriteSession("local", "ProxyMethod=2", "ProxyHost=proxy", "ProxyLocalhost=0");
        WriteSession("cmd", "ProxyMethod=5", "ProxyHost=proxy");

        var local = await PrepareAsync(Ssh("127.0.0.1", session: "local"));
        local.Extras.Should().NotContainKey(SshExtras.ProxyType);

        var cmd = await PrepareAsync(Ssh("server", session: "cmd"));
        cmd.Extras.Should().NotContainKey(SshExtras.ProxyType);
        SshExtras.GetNotices(cmd).Should().ContainSingle().Which.Should().Contain("LocalCommand proxy");
    }

    [Fact]
    public async Task TelnetConnections_GetAddressAndUserOnly()
    {
        WriteSession("router", "HostName=router", "PortNumber=2323", "UserName=admin", "Protocol=telnet", "Compression=1");
        var connection = new ConnectionInfo { Name = "r", Protocol = CoreProtocol.Telnet, Port = 23, PuttySession = "router", SSHOptions = "-C -L 1:a:2" };

        var parameters = await PrepareAsync(connection);

        (parameters.Hostname, parameters.Port, parameters.Username).Should().Be(("router", 2323, "admin"));
        parameters.Extras.Keys.Should().NotContain(k => k.StartsWith("ssh."));
    }

    [Fact]
    public async Task PuttySessionNode_UsesTheSessionPort()
    {
        WriteSession("web", "HostName=web", "PortNumber=2200", "Protocol=ssh");
        var tree = new PuttySessionsTree(new PuttySessionCatalog(new PuttySessionFilesProvider(_directory)));
        await tree.RefreshAsync();
        var node = tree.Root.Children.Single();

        var parameters = await PrepareAsync(node);

        (parameters.Protocol, parameters.Hostname, parameters.Port).Should().Be((ProtocolType.Ssh, "web", 2200));
    }

    [Fact]
    public async Task OtherProtocols_AreUntouched()
    {
        var rdp = new ConnectionInfo { Name = "rdp", Protocol = CoreProtocol.RDP, Hostname = "h", PuttySession = "gone", SSHOptions = "-C" };

        var parameters = await PrepareAsync(rdp);

        parameters.Extras.Keys.Should().NotContain(k => k.StartsWith("ssh."));
    }

    [Fact]
    public void WindowsRegistryShape_SettingsAreCaseInsensitive()
    {
        var settings = PuttySessionSettings.FromSession(new PuttySession("s", "h", 22, "u", "ssh",
            new Dictionary<string, string> { ["hostname"] = "h", ["compression"] = "1" }));

        settings.HostName.Should().Be("h");
        settings.Compression.Should().BeTrue();
    }
}
