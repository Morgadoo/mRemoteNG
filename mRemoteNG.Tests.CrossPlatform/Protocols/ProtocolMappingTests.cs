using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Shell;
using mRemoteNG.Protocols.Telnet;
using mRemoteNG.Protocols.Web;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;
using Keys = mRemoteNG.Protocols.Abstractions.ConnectionParametersFactory.Keys;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols;

/// <summary>Core → cross-platform protocol mapping and the RAW, local shell and browser protocols.</summary>
public class ProtocolMappingTests
{
    [Theory]
    [InlineData(CoreProtocol.RDP, ProtocolType.Rdp)]
    [InlineData(CoreProtocol.VNC, ProtocolType.Vnc)]
    [InlineData(CoreProtocol.ARD, ProtocolType.Vnc)]
    [InlineData(CoreProtocol.SSH1, ProtocolType.Ssh)]
    [InlineData(CoreProtocol.SSH2, ProtocolType.Ssh)]
    [InlineData(CoreProtocol.Telnet, ProtocolType.Telnet)]
    [InlineData(CoreProtocol.Rlogin, ProtocolType.Rlogin)]
    [InlineData(CoreProtocol.RAW, ProtocolType.Raw)]
    [InlineData(CoreProtocol.HTTP, ProtocolType.Http)]
    [InlineData(CoreProtocol.HTTPS, ProtocolType.Https)]
    [InlineData(CoreProtocol.PowerShell, ProtocolType.PowerShell)]
    [InlineData(CoreProtocol.AnyDesk, ProtocolType.ExternalApp)]
    [InlineData(CoreProtocol.Terminal, ProtocolType.LocalShell)]
    [InlineData(CoreProtocol.WSL, ProtocolType.LocalShell)]
    [InlineData(CoreProtocol.IntApp, ProtocolType.IntApp)]
    public void MapProtocol_MapsEveryPortedProtocol(CoreProtocol core, ProtocolType expected) =>
        ConnectionParametersFactory.MapProtocol(core).Should().Be(expected);

    [Fact]
    public void MapProtocol_EveryCoreProtocolIsPorted() =>
        Enum.GetValues<CoreProtocol>().Should().OnlyContain(p => ConnectionParametersFactory.MapProtocol(p) != null);

    [Fact]
    public void FromConnectionInfo_Ard_UsesVncPort5900()
    {
        var p = ConnectionParametersFactory.FromConnectionInfo(new ConnectionInfo { Protocol = CoreProtocol.ARD, Hostname = "mac", Port = 0 });

        p.Protocol.Should().Be(ProtocolType.Vnc);
        p.Port.Should().Be(5900);
    }

    [Fact]
    public void FromConnectionInfo_AnyDesk_LaunchesAnyDeskWithId()
    {
        var p = ConnectionParametersFactory.FromConnectionInfo(new ConnectionInfo { Protocol = CoreProtocol.AnyDesk, Hostname = "123456789" });

        p.Protocol.Should().Be(ProtocolType.ExternalApp);
        p.Extras[Keys.ExternalCommand].Should().Be("anydesk {hostname}");
    }

    [Theory]
    [InlineData(CoreProtocol.Terminal, "terminal")]
    [InlineData(CoreProtocol.WSL, "wsl")]
    public void FromConnectionInfo_LocalShell_CarriesMode(CoreProtocol core, string mode)
    {
        var p = ConnectionParametersFactory.FromConnectionInfo(new ConnectionInfo { Protocol = core, Hostname = "" });

        p.Extras[Keys.LocalShellMode].Should().Be(mode);
    }

    // ── Local shell ───────────────────────────────────────────────────────

    [Fact]
    public void LocalShell_TerminalWithHost_RunsSsh()
    {
        var p = new ConnectionParameters { Hostname = "srv", Port = 2222, Protocol = ProtocolType.LocalShell, Username = "bob" };

        LocalShellProtocol.BuildTerminalCommand(p).Should().Equal("ssh", "-p", "2222", "bob@srv");
    }

    [Fact]
    public void LocalShell_TerminalLocalhost_RunsInteractiveShell()
    {
        var p = new ConnectionParameters { Hostname = "localhost", Port = 0, Protocol = ProtocolType.LocalShell };

        var command = LocalShellProtocol.BuildTerminalCommand(p);

        command.Should().NotContain("ssh");
        if (!OperatingSystem.IsWindows())
            command.Should().EndWith("-i");
    }

    [Fact]
    public void LocalShell_Wsl_OutsideWindows_IsNotSupported()
    {
        if (OperatingSystem.IsWindows()) return;
        var p = new ConnectionParameters { Hostname = "Ubuntu", Port = 0, Protocol = ProtocolType.LocalShell };

        var act = () => LocalShellProtocol.BuildWslCommand(p);

        act.Should().Throw<PlatformNotSupportedException>();
    }

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("user@host", "user@host")]
    [InlineData("a b", "'a b'")]
    [InlineData("it's", "'it'\\''s'")]
    public void LocalShell_ShellQuote(string input, string expected) =>
        LocalShellProtocol.ShellQuote(input).Should().Be(expected);

    // ── HTTP / HTTPS ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(ProtocolType.Http, "intranet", 80, "http://intranet/")]
    [InlineData(ProtocolType.Https, "intranet", 443, "https://intranet/")]
    [InlineData(ProtocolType.Http, "intranet", 8080, "http://intranet:8080/")]
    [InlineData(ProtocolType.Http, "intranet/status", 8080, "http://intranet:8080/status")]
    [InlineData(ProtocolType.Http, "intranet:9000/x", 80, "http://intranet:9000/x")]
    [InlineData(ProtocolType.Http, "fe80::1", 80, "http://[fe80::1]/")]
    [InlineData(ProtocolType.Http, "https://site.example/path", 80, "https://site.example/path")]
    [InlineData(ProtocolType.Https, "https://site.example:8443/", 443, "https://site.example:8443/")]
    [InlineData(ProtocolType.Https, "https://site.example/", 8443, "https://site.example:8443/")]
    public void WebUrlBuilder_BuildsUrl(ProtocolType protocol, string host, int port, string expected)
    {
        var p = new ConnectionParameters { Hostname = host, Port = port, Protocol = protocol };

        WebUrlBuilder.Build(p).Should().Be(expected);
    }

    [Fact]
    public async Task ExternalBrowser_Launched_EndsDisconnectedWithoutPretending()
    {
        string? launched = null;
        using var protocol = new ExternalBrowserProtocol(NullLogger<ExternalBrowserProtocol>.Instance, url =>
        {
            launched = url;
            return true;
        });
        var states = new List<ConnectionState>();
        protocol.StateChanged += (_, s) => states.Add(s);

        await protocol.ConnectAsync(new ConnectionParameters { Hostname = "example.com", Port = 443, Protocol = ProtocolType.Https });

        launched.Should().Be("https://example.com/");
        states.Should().Equal(ConnectionState.Connecting, ConnectionState.Connected, ConnectionState.Disconnected);
    }

    [Fact]
    public async Task ExternalBrowser_LaunchFails_ReportsError()
    {
        using var protocol = new ExternalBrowserProtocol(NullLogger<ExternalBrowserProtocol>.Instance,
            _ => throw new InvalidOperationException("no browser"));
        var messages = new List<string>();
        protocol.StatusMessage += (_, m) => messages.Add(m);

        await protocol.ConnectAsync(new ConnectionParameters { Hostname = "example.com", Port = 80, Protocol = ProtocolType.Http });

        protocol.State.Should().Be(ConnectionState.Error);
        messages.Should().Contain(m => m.Contains("no browser"));
    }

    // ── RAW ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RawSocket_SendsLocallyEditedLines()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var protocol = new RawSocketProtocol(NullLogger<RawSocketProtocol>.Instance);
        var acceptTask = listener.AcceptTcpClientAsync();
        await protocol.ConnectAsync(new ConnectionParameters { Hostname = "127.0.0.1", Port = port, Protocol = ProtocolType.Raw });
        using var server = await acceptTask;
        protocol.State.Should().Be(ConnectionState.Connected);

        await protocol.HandleInputAsync("helo"u8.ToArray());
        await protocol.HandleInputAsync([0x7f]);             // backspace removes 'o'
        await protocol.HandleInputAsync("lo"u8.ToArray());
        await protocol.HandleInputAsync("\x1b[A"u8.ToArray()); // cursor key ignored
        await protocol.HandleInputAsync("\r"u8.ToArray());

        var buffer = new byte[64];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = await server.GetStream().ReadAsync(buffer, timeout.Token);

        Encoding.UTF8.GetString(buffer, 0, read).Should().Be("hello\r\n");
        await protocol.DisconnectAsync();
        protocol.State.Should().Be(ConnectionState.Disconnected);
    }
}
