using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Telnet;
using Xunit;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Integration;

/// <summary>
/// Telnet against a real inetutils telnetd (served by socat, running /bin/sh instead of login)
/// and rlogin against an in-process fake server.
/// </summary>
public sealed class TelnetIntegrationTests : IDisposable
{
    private const string Socat = "/usr/bin/socat";
    private const string Telnetd = "/usr/sbin/telnetd";
    private Process? _server;

    public void Dispose()
    {
        try { _server?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _server?.Dispose();
    }

    private int StartTelnetd()
    {
        Skip.IfNot(OperatingSystem.IsLinux() && File.Exists(Socat) && File.Exists(Telnetd), "socat and telnetd are required");

        var port = FreePort();
        _server = Process.Start(new ProcessStartInfo(Socat,
            $"TCP-LISTEN:{port},bind=127.0.0.1,reuseaddr,fork \"EXEC:{Telnetd} -h -E /bin/sh,nofork\"")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var probe = new TcpClient("127.0.0.1", port);
                return port;
            }
            catch (SocketException)
            {
                Thread.Sleep(50);
            }
        }
        throw new TimeoutException("telnetd did not start");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> WaitForOutputAsync(StringBuilder output, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (output)
            {
                if (output.ToString().Contains(expected)) return output.ToString();
            }
            await Task.Delay(50);
        }
        lock (output) return output.ToString();
    }

    [SkippableFact]
    public async Task Telnet_KeystrokesReachRealShellAndOutputComesBack()
    {
        var port = StartTelnetd();
        using var telnet = new TelnetProtocol(NullLogger<TelnetProtocol>.Instance);
        var output = new StringBuilder();
        telnet.TextReceived += text => { lock (output) output.Append(text); };

        await telnet.ConnectAsync(new ConnectionParameters { Hostname = "127.0.0.1", Port = port, Protocol = ProtocolType.Telnet });
        await telnet.SendInputAsync(Encoding.ASCII.GetBytes("echo marker-$((6*7))\r"));

        (await WaitForOutputAsync(output, "marker-42", TimeSpan.FromSeconds(10))).Should().Contain("marker-42");
        telnet.State.Should().Be(ConnectionState.Connected);
    }

    [SkippableFact]
    public async Task Telnet_WindowSizeReachesServerAndFollowsResize()
    {
        var port = StartTelnetd();
        using var telnet = new TelnetProtocol(NullLogger<TelnetProtocol>.Instance);
        var output = new StringBuilder();
        telnet.TextReceived += text => { lock (output) output.Append(text); };

        await telnet.ConnectAsync(new ConnectionParameters { Hostname = "127.0.0.1", Port = port, Protocol = ProtocolType.Telnet });
        await telnet.SendInputAsync(Encoding.ASCII.GetBytes("stty size\r"));
        (await WaitForOutputAsync(output, "24 80", TimeSpan.FromSeconds(10))).Should().Contain("24 80");

        await telnet.ResizeAsync(132, 43);
        await telnet.SendInputAsync(Encoding.ASCII.GetBytes("stty size\r"));
        (await WaitForOutputAsync(output, "43 132", TimeSpan.FromSeconds(10))).Should().Contain("43 132");
    }

    [Fact]
    public async Task Rlogin_SendsHandshakeThenRelaysInputAndUtf8Output()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var handshake = new byte[RloginProtocol.BuildHandshake(Environment.UserName, "bob").Length];
            await stream.ReadExactlyAsync(handshake);
            await stream.WriteAsync(new byte[] { 0 });

            // "é" split across two writes must still arrive as one character.
            var utf8 = Encoding.UTF8.GetBytes("café\n");
            await stream.WriteAsync(utf8.AsMemory(0, 4));
            await stream.FlushAsync();
            await Task.Delay(100);
            await stream.WriteAsync(utf8.AsMemory(4));

            var input = new byte[3];
            await stream.ReadExactlyAsync(input);
            return (handshake, input);
        });

        using var rlogin = new RloginProtocol(NullLogger<RloginProtocol>.Instance);
        var output = new StringBuilder();
        rlogin.TextReceived += text => { lock (output) output.Append(text); };

        await rlogin.ConnectAsync(new ConnectionParameters
        {
            Hostname = "127.0.0.1", Port = port, Protocol = ProtocolType.Rlogin, Username = "bob",
        });
        await rlogin.SendInputAsync("ls\r"u8.ToArray());

        var (handshake, input) = await server.WaitAsync(TimeSpan.FromSeconds(10));
        listener.Stop();

        handshake.Should().Equal(RloginProtocol.BuildHandshake(Environment.UserName, "bob"));
        Encoding.ASCII.GetString(handshake).Should().Contain("\0bob\0xterm-256color/38400\0");
        input.Should().Equal("ls\r"u8.ToArray());
        (await WaitForOutputAsync(output, "café", TimeSpan.FromSeconds(5))).Should().Contain("café");
    }

    [Fact]
    public async Task Rlogin_RejectedHandshake_ReportsError()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.ReadAsync(new byte[256]);
            await stream.WriteAsync(new byte[] { 1 });
            await Task.Delay(200);
        });

        using var rlogin = new RloginProtocol(NullLogger<RloginProtocol>.Instance);
        var act = () => rlogin.ConnectAsync(new ConnectionParameters { Hostname = "127.0.0.1", Port = port, Protocol = ProtocolType.Rlogin });

        await act.Should().ThrowAsync<InvalidOperationException>();
        rlogin.State.Should().Be(ConnectionState.Error);
        await server;
        listener.Stop();
    }
}
