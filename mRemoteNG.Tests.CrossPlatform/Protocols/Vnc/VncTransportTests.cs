using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Vnc;
using mRemoteNG.Protocols.Vnc.Rfb;
using Xunit;
using Keys = mRemoteNG.Protocols.Abstractions.ConnectionParametersFactory.Keys;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary>
/// VNC through HTTP CONNECT, SOCKS5 and UltraVNC repeaters. The fake server first plays the proxy, then — on the
/// same connection, as a real proxy would relay — the VNC server.
/// </summary>
public sealed class VncTransportTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly FakeRfbServer _proxy = new();
    private readonly VncProtocol _protocol = new(NullLogger<VncProtocol>.Instance);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _protocol.Dispose();
        await _proxy.DisposeAsync();
    }

    private ConnectionParameters Parameters(string host, int port, string proxyType, string? user = null, string? password = null)
    {
        var extras = new Dictionary<string, string>
        {
            [Keys.VncProxyType] = proxyType,
            [Keys.VncProxyHost] = "127.0.0.1",
            [Keys.VncProxyPort] = _proxy.Port.ToString(),
        };
        if (user is not null) extras[Keys.VncProxyUsername] = user;
        if (password is not null) extras[Keys.VncProxyPassword] = password;
        return new ConnectionParameters { Hostname = host, Port = port, Protocol = ProtocolType.Vnc, Extras = extras };
    }

    /// <summary>The VNC server side after the proxy dialogue: RFB 3.8, security None, a small desktop.</summary>
    private async Task ServeVncAsync(bool bannerAlreadySent = false)
    {
        if (!bannerAlreadySent)
            await _proxy.SendAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        (await _proxy.ReceiveAsync(12)).Should().Equal(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        await _proxy.SendAsync(new RfbBytes().U8(1).U8(RfbSecurityType.None));
        await _proxy.ReceiveAsync(1);
        await _proxy.SendAsync(new RfbBytes().U32(0));
        await _proxy.InitialiseAsync(40, 30, "behind proxy");
    }

    private async Task RunAsync(ConnectionParameters parameters, Func<Task> proxyScript)
    {
        var connect = _protocol.ConnectAsync(parameters);
        await _proxy.AcceptAsync();
        await proxyScript().WaitAsync(Timeout);
        await connect.WaitAsync(Timeout);
        _protocol.State.Should().Be(ConnectionState.Connected);
        _protocol.DesktopName.Should().Be("behind proxy");
    }

    // ── HTTP CONNECT ───────────────────────────────────────────────────────

    [Fact]
    public async Task Http_SendsConnectWithBasicAuth_AndKeepsBytesAfterTheHeaders()
    {
        string request = "";
        await RunAsync(Parameters("vnc.internal", 5901, "http", "proxyuser", "proxy:pass"), async () =>
        {
            request = await _proxy.ReceiveUntilAsync("\r\n\r\n");
            // The RFB banner arrives in the same packet as the response; the client must not swallow it.
            await _proxy.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\nVia: test\r\n\r\nRFB 003.008\n"));
            await ServeVncAsync(bannerAlreadySent: true);
        });

        request.Should().StartWith("CONNECT vnc.internal:5901 HTTP/1.1\r\n");
        request.Should().Contain("Host: vnc.internal:5901\r\n");
        request.Should().Contain("Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("proxyuser:proxy:pass")));
    }

    [Fact]
    public async Task Http_ProxyAuthenticationRequired_IsReported()
    {
        var connect = _protocol.ConnectAsync(Parameters("vnc.internal", 5900, "http"));
        await _proxy.AcceptAsync();
        await _proxy.ReceiveUntilAsync("\r\n\r\n");
        await _proxy.SendAsync(Encoding.ASCII.GetBytes("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic\r\n\r\n"));

        (await connect.Invoking(t => t.WaitAsync(Timeout)).Should().ThrowAsync<IOException>())
            .WithMessage("*requires authentication*407*");
        _protocol.State.Should().Be(ConnectionState.Error);
    }

    // ── SOCKS5 ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Socks5_AuthenticatesAndConnectsByHostName()
    {
        byte[] greeting = [], auth = [], request = [];
        await RunAsync(Parameters("desk.example", 5902, "socks5", "sockuser", "sockpass"), async () =>
        {
            greeting = await _proxy.ReceiveAsync(4);
            await _proxy.SendAsync([5, 2]);
            var header = await _proxy.ReceiveAsync(2);
            var user = await _proxy.ReceiveAsync(header[1]);
            var passLength = await _proxy.ReceiveAsync(1);
            var pass = await _proxy.ReceiveAsync(passLength[0]);
            auth = [.. header, .. user, .. passLength, .. pass];
            await _proxy.SendAsync([1, 0]);

            var head = await _proxy.ReceiveAsync(5);
            var rest = await _proxy.ReceiveAsync(head[4] + 2);
            request = [.. head, .. rest];
            await _proxy.SendAsync([5, 0, 0, 1, 127, 0, 0, 1, 0x17, 0x0E]);
            await ServeVncAsync();
        });

        greeting.Should().Equal(5, 2, 0, 2);
        auth.Should().Equal([1, 8, .. Encoding.ASCII.GetBytes("sockuser"), 8, .. Encoding.ASCII.GetBytes("sockpass")]);
        request.Should().Equal([5, 1, 0, 3, 12, .. Encoding.ASCII.GetBytes("desk.example"), 0x17, 0x0E]);
    }

    [Fact]
    public async Task Socks5_WithoutCredentials_ConnectsToAnIpAddress()
    {
        byte[] request = [];
        await RunAsync(Parameters("10.1.2.3", 5900, "socks5"), async () =>
        {
            (await _proxy.ReceiveAsync(3)).Should().Equal(5, 1, 0);
            await _proxy.SendAsync([5, 0]);
            request = await _proxy.ReceiveAsync(10);
            // Bound address as a domain name, which the client must skip correctly.
            await _proxy.SendAsync([5, 0, 0, 3, 4, (byte)'p', (byte)'r', (byte)'x', (byte)'y', 0, 1]);
            await ServeVncAsync();
        });

        request.Should().Equal(5, 1, 0, 1, 10, 1, 2, 3, 0x17, 0x0C);
    }

    [Fact]
    public async Task Socks5_ConnectionRefusedByProxy_IsReported()
    {
        var connect = _protocol.ConnectAsync(Parameters("10.9.9.9", 5900, "socks5"));
        await _proxy.AcceptAsync();
        await _proxy.ReceiveAsync(3);
        await _proxy.SendAsync([5, 0]);
        await _proxy.ReceiveAsync(10);
        await _proxy.SendAsync([5, 5, 0, 1, 0, 0, 0, 0, 0, 0]);

        (await connect.Invoking(t => t.WaitAsync(Timeout)).Should().ThrowAsync<IOException>())
            .WithMessage("*SOCKS5*10.9.9.9:5900*connection refused*");
    }

    // ── UltraVNC repeater ──────────────────────────────────────────────────

    [Theory]
    [InlineData("server.lan", 5900, "server.lan:5900")]
    [InlineData("ID:4321", 5900, "ID:4321")]
    public async Task Repeater_NamesTheServerIn250Bytes(string host, int port, string expected)
    {
        byte[] id = [];
        await RunAsync(Parameters(host, port, "ultravnc"), async () =>
        {
            await _proxy.SendAsync(Encoding.ASCII.GetBytes(VncTransport.RepeaterBanner));
            id = await _proxy.ReceiveAsync(VncTransport.RepeaterIdLength);
            await ServeVncAsync();
        });

        Encoding.ASCII.GetString(id).TrimEnd('\0').Should().Be(expected);
        id.Skip(expected.Length).Should().OnlyContain(b => b == 0);
    }

    [Fact]
    public async Task Repeater_ThatIsReallyAVncServer_IsReported()
    {
        var connect = _protocol.ConnectAsync(Parameters("server", 5900, "ultravnc"));
        await _proxy.AcceptAsync();
        await _proxy.SendAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"));

        (await connect.Invoking(t => t.WaitAsync(Timeout)).Should().ThrowAsync<IOException>())
            .WithMessage("*repeater*unexpected greeting*");
    }
}

/// <summary>A minimal forwarding SOCKS5 / HTTP CONNECT proxy, to reach a real VNC server through.</summary>
internal sealed class ForwardingProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;

    public ForwardingProxy(bool http)
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(http));
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int ConnectionsRelayed;

    private async Task AcceptLoopAsync(bool http)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            _ = Task.Run(() => RelayAsync(client, http));
        }
    }

    private async Task RelayAsync(TcpClient client, bool http)
    {
        using (client)
        {
            var stream = client.GetStream();
            string host;
            int port;
            if (http)
            {
                var header = new StringBuilder();
                var b = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                {
                    await stream.ReadExactlyAsync(b);
                    header.Append((char)b[0]);
                }
                var authority = header.ToString().Split(' ')[1];
                host = authority[..authority.LastIndexOf(':')];
                port = int.Parse(authority[(authority.LastIndexOf(':') + 1)..]);
            }
            else
            {
                var greeting = new byte[2];
                await stream.ReadExactlyAsync(greeting);
                await stream.ReadExactlyAsync(new byte[greeting[1]]);
                await stream.WriteAsync(new byte[] { 5, 0 });
                var head = new byte[4];
                await stream.ReadExactlyAsync(head);
                if (head[3] == 1)
                {
                    var ip = new byte[4];
                    await stream.ReadExactlyAsync(ip);
                    host = new IPAddress(ip).ToString();
                }
                else
                {
                    var length = new byte[1];
                    await stream.ReadExactlyAsync(length);
                    var name = new byte[length[0]];
                    await stream.ReadExactlyAsync(name);
                    host = Encoding.ASCII.GetString(name);
                }
                var portBytes = new byte[2];
                await stream.ReadExactlyAsync(portBytes);
                port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
            }

            using var upstream = new TcpClient();
            await upstream.ConnectAsync(host, port);
            if (http)
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"));
            else
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 });
            Interlocked.Increment(ref ConnectionsRelayed);

            var server = upstream.GetStream();
            var up = stream.CopyToAsync(server, _stop.Token);
            var down = server.CopyToAsync(stream, _stop.Token);
            try { await Task.WhenAny(up, down); }
            catch (Exception) { /* either side closed */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _acceptLoop; }
        catch (Exception) { /* stopped */ }
    }
}

/// <summary>Real VNC sessions (TigerVNC) through forwarding proxies, plus the session capabilities.</summary>
[Trait("Category", "Integration")]
[Collection(TigerVncCollection.Name)]
public sealed class VncProxyIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly TigerVncFixture _server;

    public VncProxyIntegrationTests(TigerVncFixture server) => _server = server;

    [SkippableTheory]
    [InlineData("http")]
    [InlineData("socks5")]
    public async Task VncProtocol_ThroughProxy_ReceivesTheDesktop(string proxyType)
    {
        Skip.If(_server.SkipReason is not null, _server.SkipReason);
        await using var proxy = new ForwardingProxy(http: proxyType == "http");
        using var protocol = new VncProtocol(NullLogger<VncProtocol>.Instance);

        await protocol.ConnectAsync(new ConnectionParameters
        {
            Hostname = "127.0.0.1",
            Port = TigerVncFixture.Port,
            Protocol = ProtocolType.Vnc,
            Extras = new Dictionary<string, string>
            {
                [Keys.VncProxyType] = proxyType,
                [Keys.VncProxyHost] = "127.0.0.1",
                [Keys.VncProxyPort] = proxy.Port.ToString(),
                [Keys.VncEncoding] = "tight",
                [Keys.VncCompression] = "6",
            },
        }).WaitAsync(Timeout);

        proxy.ConnectionsRelayed.Should().Be(1);
        protocol.State.Should().Be(ConnectionState.Connected);
        var deadline = DateTime.UtcNow + Timeout;
        while (!protocol.RectangleCounts.ContainsKey(RfbEncoding.Tight) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        protocol.RectangleCounts.Should().ContainKey(RfbEncoding.Tight);
        await protocol.DisconnectAsync();
    }
}
