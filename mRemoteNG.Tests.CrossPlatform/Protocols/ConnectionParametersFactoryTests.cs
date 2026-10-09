using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Container;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Rdp;
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
    [InlineData(CoreProtocol.IntApp)]
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
        extras[Keys.RdpHomeDrive].Should().Be("true");
        extras[Keys.RdpSound].Should().Be("off");
        extras[Keys.RdpGateway].Should().Be("gw.example");
    }

    [Fact]
    public void FreeRdpArgs_ValuesWithQuotesAndSpaces_StaySingleArguments()
    {
        var p = new ConnectionParameters
        {
            Hostname = "srv", Port = 3389, Protocol = ProtocolType.Rdp,
            Username = "bob\" /drive:x,/ \"", Password = "p a\"ss",
        };

        var args = RdpProtocol.BuildFreeRdpArgs(p);

        args.Should().Contain("/u:bob\" /drive:x,/ \"");
        args.Should().Contain("/p:p a\"ss");
        args.Should().NotContain(a => a.StartsWith("/drive:"));
    }

    [Fact]
    public void FreeRdpArgs_Defaults_DoNotShareFilesystem()
    {
        var p = new ConnectionParameters { Hostname = "srv", Port = 3389, Protocol = ProtocolType.Rdp };

        var args = RdpProtocol.BuildFreeRdpArgs(p);

        args.Should().NotContain(a => a.StartsWith("/drive") || a == "+home-drive");
        args.Should().Contain("/sec:nla").And.Contain("/clipboard").And.Contain("/v:srv:3389");
    }

    [Fact]
    public void FreeRdpArgs_FromConnectionInfo_AppliesRedirectionAndGateway()
    {
        var info = new ConnectionInfo
        {
            Protocol = CoreProtocol.RDP, Hostname = "srv",
            UseCredSsp = false,
            RedirectDiskDrives = RDPDiskDrives.Local,
            RedirectClipboard = false,
            RDGatewayUsageMethod = RDGatewayUsageMethod.Always,
            RDGatewayHostname = "gw",
            RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.No,
            RDGatewayUsername = "gwuser",
        };

        var args = RdpProtocol.BuildFreeRdpArgs(ConnectionParametersFactory.FromConnectionInfo(info));

        args.Should().Contain("+home-drive").And.Contain("-sec-nla").And.Contain("/gateway:g:gw,u:gwuser");
        args.Should().NotContain("/clipboard");
    }

    [Theory]
    [InlineData("This is FreeRDP version 3.5.1 (3.5.1)", 3)]
    [InlineData("This is FreeRDP version 2.6.1 (2.6.1+dfsg1-3ubuntu2)", 2)]
    [InlineData("garbage", 0)]
    public void ParseFreeRdpMajorVersion_ReadsMajor(string output, int expected)
    {
        RdpProtocol.ParseFreeRdpMajorVersion(output).Should().Be(expected);
    }
}
