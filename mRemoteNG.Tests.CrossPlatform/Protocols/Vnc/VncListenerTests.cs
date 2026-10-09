using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Vnc;
using mRemoteNG.Protocols.Vnc.Rfb;
using Xunit;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary>Reverse VNC (UltraVNC SingleClick): the server connects to a listening viewer.</summary>
public sealed class VncListenerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task AcceptedConnection_RunsTheNormalHandshake()
    {
        await using var listener = new VncListener(0, IPAddress.Loopback);
        var accepted = new TaskCompletionSource<VncIncomingConnectionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConnectionAccepted += (_, e) => accepted.TrySetResult(e);
        listener.Start();
        listener.IsListening.Should().BeTrue();

        // The "server" dials the viewer, then speaks RFB as usual.
        using var server = new TcpClient();
        await server.ConnectAsync(IPAddress.Loopback, listener.Port);
        var incoming = await accepted.Task.WaitAsync(Timeout);
        incoming.RemoteEndPoint!.Address.Should().Be(IPAddress.Loopback);

        using var protocol = new VncProtocol(NullLogger<VncProtocol>.Instance);
        protocol.UseIncomingConnection(incoming.Client);
        var connect = protocol.ConnectAsync(new ConnectionParameters
        {
            Hostname = incoming.RemoteEndPoint.Address.ToString(),
            Port = incoming.RemoteEndPoint.Port,
            Protocol = ProtocolType.Vnc,
        });

        var stream = server.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        var version = new byte[12];
        await stream.ReadExactlyAsync(version).AsTask().WaitAsync(Timeout);
        await stream.WriteAsync(new byte[] { 1, RfbSecurityType.None });
        await stream.ReadExactlyAsync(new byte[1]);
        await stream.WriteAsync(new RfbBytes().U32(0)
            .U16(20).U16(10).U8(32).U8(24).U8(0).U8(1).U16(255).U16(255).U16(255).U8(16).U8(8).U8(0).U8(0).U8(0).U8(0)
            .String("singleclick").ToArray());

        await connect.WaitAsync(Timeout);
        protocol.State.Should().Be(ConnectionState.Connected);
        protocol.DesktopName.Should().Be("singleclick");

        await listener.StopAsync();
        listener.IsListening.Should().BeFalse();
    }

    [Fact]
    public async Task WithoutHandler_ConnectionsAreClosed_AndListeningContinues()
    {
        await using var listener = new VncListener(0, IPAddress.Loopback);
        listener.Start();

        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, listener.Port);
        var read = await first.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(Timeout);
        read.Should().Be(0, "an unhandled connection is closed");

        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, listener.Port);
        second.Connected.Should().BeTrue();
    }

    [Fact]
    public void PortInUse_Throws()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        try
        {
            var listener = new VncListener(((IPEndPoint)blocker.LocalEndpoint).Port, IPAddress.Loopback);
            listener.Invoking(l => l.Start()).Should().Throw<SocketException>();
        }
        finally
        {
            blocker.Stop();
        }
    }

    /// <summary>A real reverse connection: x11vnc on an Xvfb display dials the listener (port 5501).</summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task X11vncConnect_OpensASession()
    {
        const int display = 136;
        const int port = 5501;
        var xvfbPath = ExternalProcess.FindExecutable("Xvfb");
        var x11vncPath = ExternalProcess.FindExecutable("x11vnc");
        Skip.If(xvfbPath is null || x11vncPath is null, "Xvfb / x11vnc not installed");
        Skip.If(!ExternalProcess.ClaimDisplay(display), $"display :{display} is in use");
        Skip.If(ExternalProcess.IsListening(port), $"port {port} is in use");

        var log = new StringBuilder();
        Process? xvfb = null, x11vnc = null;
        await using var listener = new VncListener(port, IPAddress.Loopback);
        try
        {
            xvfb = ExternalProcess.Start(xvfbPath!, [$":{display}", "-screen", "0", "640x480x24", "-nolisten", "tcp", "-noreset"], log);
            ExternalProcess.WaitUntil(() => File.Exists($"/tmp/.X11-unix/X{display}"), Timeout, xvfb).Should().BeTrue(log.ToString());
            ExternalProcess.RunX(display, "xsetroot", "-solid", "#336699");

            var accepted = new TaskCompletionSource<VncIncomingConnectionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.ConnectionAccepted += (_, e) => accepted.TrySetResult(e);
            listener.Start();

            x11vnc = ExternalProcess.Start(x11vncPath!,
                ["-display", $":{display}", "-connect", $"localhost:{port}", "-rfbport", "0", "-nopw", "-q", "-once"], log);
            var incoming = await accepted.Task.WaitAsync(Timeout);

            using var protocol = new VncProtocol(NullLogger<VncProtocol>.Instance);
            protocol.UseIncomingConnection(incoming.Client);
            await protocol.ConnectAsync(new ConnectionParameters
            {
                Hostname = incoming.RemoteEndPoint!.Address.ToString(),
                Port = incoming.RemoteEndPoint.Port,
                Protocol = ProtocolType.Vnc,
            }).WaitAsync(Timeout);

            protocol.State.Should().Be(ConnectionState.Connected);
            protocol.Framebuffer!.Width.Should().Be(640);
            var deadline = DateTime.UtcNow + Timeout;
            while (protocol.Framebuffer.GetPixel(320, 240) != 0xFF336699u && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            protocol.Framebuffer.GetPixel(320, 240).Should().Be(0xFF336699u, "the remote root window colour arrives");
            await protocol.DisconnectAsync();
        }
        finally
        {
            ExternalProcess.Stop(x11vnc);
            ExternalProcess.Stop(xvfb, display);
        }
    }
}
