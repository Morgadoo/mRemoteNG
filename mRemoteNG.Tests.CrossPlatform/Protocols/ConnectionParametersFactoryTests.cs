using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Container;
using mRemoteNG.Protocols.Abstractions;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;
using Keys = mRemoteNG.Protocols.Abstractions.ConnectionParametersFactory.Keys;

namespace mRemoteNG.Tests.CrossPlatform.Protocols;

public class ConnectionParametersFactoryTests
{
    [Fact]
    public void FromConnectionInfo_CarriesCredentials()
    {
        var info = new ConnectionInfo
        {
            Protocol = CoreProtocol.SSH2, Hostname = "srv", Port = 2222,
            Username = "alice", Password = "pw", Domain = "CORP",
        };

        var p = ConnectionParametersFactory.FromConnectionInfo(info);

        p.Protocol.Should().Be(ProtocolType.Ssh);
        p.Hostname.Should().Be("srv");
        p.Port.Should().Be(2222);
        p.Username.Should().Be("alice");
        p.Password.Should().Be("pw");
        p.Domain.Should().Be("CORP");
    }

    [Fact]
    public void FromConnectionInfo_ResolvesInheritedCredentials()
    {
        var folder = new ContainerInfo { Name = "folder", Username = "inherited-user", Password = "inherited-pw" };
        var info = new ConnectionInfo { Protocol = CoreProtocol.RDP, Hostname = "srv", Username = "own", Password = "own" };
        folder.AddChild(info);
        info.Inheritance.Username = true;
        info.Inheritance.Password = true;

        var p = ConnectionParametersFactory.FromConnectionInfo(info);

        p.Username.Should().Be("inherited-user");
        p.Password.Should().Be("inherited-pw");
    }

    [Fact]
    public void FromConnectionInfo_ZeroPort_UsesProtocolDefault()
    {
        var info = new ConnectionInfo { Protocol = CoreProtocol.RDP, Hostname = "srv", Port = 0 };

        ConnectionParametersFactory.FromConnectionInfo(info).Port.Should().Be(3389);
    }

    [Theory]
    [InlineData((CoreProtocol)999)]
    public void FromConnectionInfo_UnsupportedProtocol_Throws(CoreProtocol protocol)
    {
        var act = () => ConnectionParametersFactory.FromConnectionInfo(new ConnectionInfo { Protocol = protocol, Hostname = "srv" });

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void FromConnectionInfo_MapsRdpOptions()
    {
        var info = new ConnectionInfo
        {
            Protocol = CoreProtocol.RDP, Hostname = "srv",
            Colors = RDPColors.Colors16Bit,
            UseCredSsp = false,
            UseConsoleSession = true,
            RedirectClipboard = false,
            RedirectDiskDrives = RDPDiskDrives.Local,
            RedirectSound = RDPSounds.DoNotPlay,
            RDGatewayUsageMethod = RDGatewayUsageMethod.Always,
            RDGatewayHostname = "gw.example",
        };

        var extras = ConnectionParametersFactory.FromConnectionInfo(info).Extras;

        extras[Keys.RdpColorDepth].Should().Be("16");
        extras[Keys.RdpNla].Should().Be("false");
        extras[Keys.RdpConsole].Should().Be("true");
        extras[Keys.RdpClipboard].Should().Be("false");
        extras[Keys.RdpDrives].Should().Be("local");
        extras[Keys.RdpSound].Should().Be("off");
        extras[Keys.RdpGateway].Should().Be("gw.example");
    }
}
