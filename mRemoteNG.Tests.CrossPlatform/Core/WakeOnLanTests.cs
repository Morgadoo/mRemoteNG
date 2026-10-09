using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using mRemoteNG.Core.Net;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core;

public class WakeOnLanTests
{
    private static readonly byte[] Mac = [0x00, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E];

    [Theory]
    [InlineData("00:1a:2b:3c:4d:5e")]
    [InlineData("00-1A-2B-3C-4D-5E")]
    [InlineData("001a.2b3c.4d5e")]
    [InlineData("001A2B3C4D5E")]
    [InlineData("  00:1A:2B:3C:4D:5E ")]
    public void TryParseMacAddress_AcceptsCommonFormats(string text)
    {
        WakeOnLan.TryParseMacAddress(text, out var mac).Should().BeTrue();
        mac.Should().Equal(Mac);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00:1A:2B:3C:4D")]
    [InlineData("00:1A:2B:3C:4D:5E:6F")]
    [InlineData("00:1A:2B:3C:4D:ZZ")]
    [InlineData("00 1A 2B 3C 4D 5E")]
    public void TryParseMacAddress_RejectsInvalidText(string? text)
    {
        WakeOnLan.TryParseMacAddress(text, out _).Should().BeFalse();
    }

    [Fact]
    public void MagicPacket_IsSixFFsFollowedBySixteenCopiesOfTheMac()
    {
        var packet = WakeOnLan.BuildMagicPacket(Mac);

        packet.Should().HaveCount(102);
        packet.Take(6).Should().OnlyContain(b => b == 0xFF);
        for (var i = 0; i < 16; i++)
            packet.Skip(6 + i * 6).Take(6).Should().Equal(Mac);
    }

    [Fact]
    public async Task SendAsync_DeliversTheMagicPacketOverUdp()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)listener.Client.LocalEndPoint!;

        await WakeOnLan.SendAsync("00:1a:2b:3c:4d:5e", endpoint);
        var received = await listener.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));

        received.Buffer.Should().Equal(WakeOnLan.BuildMagicPacket(Mac));
    }

    [Fact]
    public async Task SendAsync_InvalidMac_Throws()
    {
        var act = () => WakeOnLan.SendAsync("nope", new IPEndPoint(IPAddress.Loopback, 9));

        await act.Should().ThrowAsync<FormatException>();
    }

    [Fact]
    public async Task HostStatusProbe_ReportsAnOpenAndAClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var open = await HostStatusProbe.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(3));
            open.PortOpen.Should().BeTrue();
            open.IsReachable.Should().BeTrue();
            open.Summary.Should().StartWith("Online").And.Contain($"port {port} open");
        }
        finally
        {
            listener.Stop();
        }

        var closed = await HostStatusProbe.ProbeAsync("127.0.0.1", port, TimeSpan.FromSeconds(3));
        closed.PortOpen.Should().BeFalse();
        closed.PortError.Should().NotBeNullOrEmpty();
    }
}
