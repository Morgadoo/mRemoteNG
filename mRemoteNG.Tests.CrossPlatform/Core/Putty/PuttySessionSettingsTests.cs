using FluentAssertions;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Config.Putty;
using mRemoteNG.Core.Tree;
using mRemoteNG.Platform;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Core.Putty;

public sealed class PuttySessionSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "putty-sessions-" + Guid.NewGuid().ToString("N"));

    public PuttySessionSettingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>Writes a session file the way PuTTY on Unix does (URL-encoded name, Key=Value lines).</summary>
    private void WriteSession(string name, params string[] lines) =>
        File.WriteAllLines(Path.Combine(_directory, Uri.EscapeDataString(name)), lines);

    [Fact]
    public void SessionFile_IsReadWithAllSettings()
    {
        WriteSession("Jump host",
            "HostName=jump.example.com",
            "PortNumber=2200",
            "UserName=alice",
            "Protocol=ssh",
            "PublicKeyFile=/home/alice/.ssh/jump.ppk",
            "Compression=1",
            "AgentFwd=1",
            "X11Forward=0",
            "LocalPortAcceptAll=0",
            "RemotePortAcceptAll=1",
            @"PortForwardings=L8080=intranet:80,4L127.0.0.2:8081=[fe80::1]:81,R2222=localhost:22,D1080=,L1081=D,Lbad=x:1,R9=nohostport",
            "ProxyMethod=2",
            "ProxyHost=proxy.example.com",
            "ProxyPort=1081",
            "ProxyUsername=pu",
            "ProxyPassword=pp",
            "ProxyExcludeList=*.internal, 10.*",
            "ProxyLocalhost=0");

        var session = PuttySessionFilesProvider.ReadSessions(_directory).Should().ContainSingle().Subject;
        var settings = PuttySessionSettings.FromSession(session);

        settings.Name.Should().Be("Jump host");
        settings.HostName.Should().Be("jump.example.com");
        settings.PortNumber.Should().Be(2200);
        settings.UserName.Should().Be("alice");
        settings.PublicKeyFile.Should().Be("/home/alice/.ssh/jump.ppk");
        settings.Compression.Should().BeTrue();
        settings.AgentForwarding.Should().BeTrue();
        settings.X11Forwarding.Should().BeFalse();
        settings.PortForwardings.Should().Equal(
            new PortForwardSpec(PortForwardKind.Local, null, 8080, "intranet", 80),
            new PortForwardSpec(PortForwardKind.Local, "127.0.0.2", 8081, "fe80::1", 81),
            new PortForwardSpec(PortForwardKind.Remote, "0.0.0.0", 2222, "localhost", 22),
            new PortForwardSpec(PortForwardKind.Dynamic, null, 1080),
            new PortForwardSpec(PortForwardKind.Dynamic, null, 1081));

        var proxy = settings.Proxy;
        proxy.Should().Be(new PuttyProxySettings(PuttyProxyMethod.Socks5, "proxy.example.com", 1081, "pu", "pp", "*.internal, 10.*", false));
        proxy.AppliesTo("server.example.com").Should().BeTrue();
        proxy.AppliesTo("db.internal").Should().BeFalse();
        proxy.AppliesTo("10.1.2.3").Should().BeFalse();
        proxy.AppliesTo("localhost").Should().BeFalse();
        (proxy with { ProxyLocalhost = true }).AppliesTo("127.0.0.1").Should().BeTrue();
        (proxy with { Method = PuttyProxyMethod.None }).AppliesTo("server.example.com").Should().BeFalse();
    }

    [Fact]
    public void PortForwardings_EscapesAndAcceptAll()
    {
        PuttySessionSettings.ParsePortForwardings(@"L8080=we\,ird:80,D1080", localAcceptAll: true)
            .Should().Equal(
                new PortForwardSpec(PortForwardKind.Local, "0.0.0.0", 8080, "we,ird", 80),
                new PortForwardSpec(PortForwardKind.Dynamic, "0.0.0.0", 1080));
        PuttySessionSettings.ParsePortForwardings("").Should().BeEmpty();
    }

    [Fact]
    public void FromSession_WithoutStoredSettings_UsesSummaryFields()
    {
        var settings = PuttySessionSettings.FromSession(new PuttySession("s", "h", 2022, "u", "telnet"));

        (settings.HostName, settings.PortNumber, settings.UserName, settings.Protocol).Should().Be(("h", 2022, "u", "telnet"));
        settings.PortForwardings.Should().BeEmpty();
    }

    [Fact]
    public async Task Catalog_FindsSessionsByName()
    {
        WriteSession("web", "HostName=web.example.com");
        var catalog = new PuttySessionCatalog(new PuttySessionFilesProvider(_directory));

        (await catalog.FindAsync("web"))!.HostName.Should().Be("web.example.com");
        (await catalog.FindAsync("WEB"))!.HostName.Should().Be("web.example.com");
        (await catalog.FindAsync("missing")).Should().BeNull();
        (await new PuttySessionCatalog(new PuttySessionFilesProvider(Path.Combine(_directory, "none"))).GetSessionsAsync())
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Tree_ShowsSessions_AndFollowsChanges()
    {
        WriteSession("Default Settings", "HostName=", "Protocol=ssh");
        WriteSession("web", "HostName=web.example.com", "PortNumber=2222", "UserName=www", "Protocol=ssh");
        WriteSession("router", "HostName=10.0.0.1", "Protocol=telnet");
        WriteSession("console", "Protocol=serial");
        var tree = new PuttySessionsTree(new PuttySessionCatalog(new PuttySessionFilesProvider(_directory)));
        var changes = 0;
        tree.Changed += (_, _) => changes++;

        (await tree.RefreshAsync()).Should().BeTrue();

        tree.Root.Name.Should().Be("PuTTY Sessions");
        var nodes = tree.Root.Children.Cast<PuttySessionNodeInfo>().ToList();
        nodes.Select(n => n.Name).Should().Equal("router", "web");
        var web = nodes[1];
        (web.Hostname, web.Port, web.Username, web.Protocol, web.PuttySession).Should().Be(("web.example.com", 2222, "www", CoreProtocol.SSH2, "web"));
        web.GetTreeNodeType().Should().Be(TreeNodeType.PuttySession);
        web.Parent.Should().BeSameAs(tree.Root);
        nodes[0].Port.Should().Be(23);

        // Unchanged: nothing happens.
        (await tree.RefreshAsync()).Should().BeFalse();

        // Changed in PuTTY, one removed, one added: existing nodes keep their identity.
        WriteSession("web", "HostName=web2.example.com", "PortNumber=2222", "UserName=www", "Protocol=ssh");
        File.Delete(Path.Combine(_directory, "router"));
        WriteSession("db", "HostName=db.example.com", "Protocol=ssh");
        (await tree.RefreshAsync()).Should().BeTrue();

        tree.Root.Children.Select(n => n.Name).Should().Equal("db", "web");
        tree.Root.Children[1].Should().BeSameAs(web);
        web.Hostname.Should().Be("web2.example.com");
        changes.Should().Be(2);

        var copy = web.Clone();
        copy.Should().NotBeOfType<PuttySessionNodeInfo>();
        copy.Hostname.Should().Be("web2.example.com");
    }
}
