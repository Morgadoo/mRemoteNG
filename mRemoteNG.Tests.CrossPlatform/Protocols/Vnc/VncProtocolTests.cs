using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol.VNC;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Vnc;
using mRemoteNG.Protocols.Vnc.Rfb;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;
using Keys = mRemoteNG.Protocols.Abstractions.ConnectionParametersFactory.Keys;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

public sealed class VncProtocolTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly FakeRfbServer _server = new();
    private readonly VncProtocol _protocol = new(NullLogger<VncProtocol>.Instance);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _protocol.Dispose();
        await _server.DisposeAsync();
    }

    private ConnectionParameters Parameters(string? password = null, Dictionary<string, string>? extras = null) => new()
    {
        Hostname = "127.0.0.1",
        Port = _server.Port,
        Protocol = ProtocolType.Vnc,
        Password = password,
        Extras = extras ?? new Dictionary<string, string>(),
    };

    // ── Extras mapping ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(VncSmartSizeMode.SmartSNo, "none")]
    [InlineData(VncSmartSizeMode.SmartSFree, "stretch")]
    [InlineData(VncSmartSizeMode.SmartSAspect, "fit")]
    public void ConnectionParametersFactory_MapsSmartSizeMode(VncSmartSizeMode mode, string expected)
    {
        var info = new ConnectionInfo { Protocol = CoreProtocol.VNC, Hostname = "h", VNCSmartSizeMode = mode, VNCViewOnly = true };

        var extras = ConnectionParametersFactory.FromConnectionInfo(info).Extras;

        extras[Keys.VncScaling].Should().Be(expected);
        extras[Keys.VncViewOnly].Should().Be("true");
    }

    [Theory]
    [InlineData("none", VncScaling.None)]
    [InlineData("Stretch", VncScaling.Stretch)]
    [InlineData("fit", VncScaling.Fit)]
    [InlineData(null, VncScaling.Fit)]
    [InlineData("bogus", VncScaling.Fit)]
    public void ParseScaling_DefaultsToFit(string? value, VncScaling expected)
    {
        VncProtocol.ParseScaling(value).Should().Be(expected);
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Connect_StaysConnectingUntilServerInit_ThenConnected()
    {
        var states = new List<ConnectionState>();
        var connect = _protocol.ConnectAsync(Parameters(extras: new()
        {
            [Keys.VncScaling] = "stretch",
            [Keys.VncViewOnly] = "true",
        }));

        await _server.AcceptAsync();
        await _server.NegotiateVersionAsync("003.008");
        await _server.SendAsync(new RfbBytes().U8(1).U8(RfbSecurityType.None));
        await _server.ReceiveAsync(1);
        await _server.SendAsync(new RfbBytes().U32(0));
        await Task.Delay(100);
        _protocol.State.Should().Be(ConnectionState.Connecting, "ServerInit has not arrived yet");
        connect.IsCompleted.Should().BeFalse();

        await _server.InitialiseAsync(320, 240, "fake");
        await connect.WaitAsync(Timeout);

        _protocol.State.Should().Be(ConnectionState.Connected);
        _protocol.Framebuffer!.Width.Should().Be(320);
        _protocol.DesktopName.Should().Be("fake");
        _protocol.Scaling.Should().Be(VncScaling.Stretch);
        _protocol.ViewOnly.Should().BeTrue();

        // The receive loop has started: it asks for the full framebuffer.
        (await _server.ReceiveAsync(10))[0].Should().Be(3);

        await _protocol.DisconnectAsync().WaitAsync(Timeout);
        _protocol.State.Should().Be(ConnectionState.Disconnected);
        _protocol.Framebuffer.Should().BeNull();
    }

    [Fact]
    public async Task Connect_AuthenticationFailure_SetsErrorAndThrows()
    {
        var connect = _protocol.ConnectAsync(Parameters(password: "nope"));
        await _server.AcceptAsync();
        await _server.NegotiateVersionAsync("003.008");
        await _server.SendAsync(new RfbBytes().U8(1).U8(RfbSecurityType.VncAuthentication));
        await _server.ReceiveAsync(1);
        await _server.SendAsync(new byte[16]);
        await _server.ReceiveAsync(16);
        await _server.SendAsync(new RfbBytes().U32(1).String("Authentication failure"));

        await connect.Invoking(t => t.WaitAsync(Timeout))
            .Should().ThrowAsync<RfbAuthenticationException>().WithMessage("*Authentication failure*");
        _protocol.State.Should().Be(ConnectionState.Error);
    }

    [Fact]
    public async Task Connect_RefusedPort_SetsErrorWithHostInMessage()
    {
        // Grab a free port, then close it so nothing listens there.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var act = () => _protocol.ConnectAsync(new ConnectionParameters
        {
            Hostname = "127.0.0.1", Port = port, Protocol = ProtocolType.Vnc,
        });

        (await act.Should().ThrowAsync<IOException>()).WithMessage($"*127.0.0.1:{port}*");
        _protocol.State.Should().Be(ConnectionState.Error);
    }

    [Fact]
    public async Task ServerDisconnect_AfterConnect_SetsError()
    {
        var connect = _protocol.ConnectAsync(Parameters());
        await _server.AcceptAsync();
        await _server.NegotiateVersionAsync("003.003");
        await _server.SendAsync(new RfbBytes().U32(RfbSecurityType.None));
        await _server.InitialiseAsync(16, 16, "x");
        await connect.WaitAsync(Timeout);
        await _server.ReceiveAsync(10);

        _server.CloseConnection();

        var deadline = DateTime.UtcNow + Timeout;
        while (_protocol.State != ConnectionState.Error && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        _protocol.State.Should().Be(ConnectionState.Error);
    }

    [Fact]
    public async Task Dispose_WhileConnected_IsSafe()
    {
        var connect = _protocol.ConnectAsync(Parameters());
        await _server.AcceptAsync();
        await _server.NegotiateVersionAsync("003.003");
        await _server.SendAsync(new RfbBytes().U32(RfbSecurityType.None));
        await _server.InitialiseAsync(16, 16, "x");
        await connect.WaitAsync(Timeout);

        _protocol.Dispose();
        _protocol.Dispose();

        await _protocol.DisconnectAsync().WaitAsync(Timeout);
        _protocol.State.Should().Be(ConnectionState.Disconnected);
    }
}
