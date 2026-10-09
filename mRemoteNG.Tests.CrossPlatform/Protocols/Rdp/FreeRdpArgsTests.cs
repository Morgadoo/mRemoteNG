using Avalonia;
using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Rdp;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;
using Keys = mRemoteNG.Protocols.Abstractions.ConnectionParametersFactory.Keys;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Rdp;

public class FreeRdpArgsTests
{
    private static ConnectionParameters Params(Dictionary<string, string>? extras = null) => new()
    {
        Hostname = "srv",
        Port = 3389,
        Protocol = ProtocolType.Rdp,
        Extras = extras ?? new Dictionary<string, string>(),
    };

    [Fact]
    public void ValuesWithQuotesAndSpaces_StaySingleArguments()
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
    public void Defaults_DoNotShareFilesystem_AndNegotiateSecurity()
    {
        var args = RdpProtocol.BuildFreeRdpArgs(Params());

        args.Should().NotContain(a => a.StartsWith("/drive") || a == "+home-drive");
        args.Should().Contain("/clipboard").And.Contain("/v:srv:3389").And.Contain("/dynamic-resolution");
        // NLA allowed (mstsc "Use CredSSP" semantics): FreeRDP negotiates NLA > TLS > RDP, nothing forced.
        args.Should().NotContain(a => a.StartsWith("/sec") || a.Contains("sec-nla"));
        args.Should().Contain("/cert:tofu");
        // Audio backend left to FreeRDP's auto-detection (forcing sys:pulse aborts when pulse is not running).
        args.Should().Contain("/sound");
    }

    [Fact]
    public void NoPassword_PassesExplicitEmptyPassword_SoFreeRdpDoesNotPromptOnItsTerminal()
    {
        var args = RdpProtocol.BuildFreeRdpArgs(Params());

        args.Should().ContainSingle(a => a.StartsWith("/p:")).Which.Should().Be("/p:");
    }

    [Fact]
    public void FromConnectionInfo_AppliesRedirectionAndGateway()
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

        var args = RdpProtocol.BuildFreeRdpArgs(ConnectionParametersFactory.FromConnectionInfo(info),
            new FreeRdpLaunchOptions { WindowsClient = false });

        args.Should().Contain("+home-drive").And.Contain("/sec:nla:off").And.Contain("/gateway:g:gw,u:gwuser");
        args.Should().NotContain("/clipboard");
    }

    [Fact]
    public void NlaDisabled_UsesVersionSpecificSyntax()
    {
        var p = Params(new() { [Keys.RdpNla] = "false" });

        RdpProtocol.BuildFreeRdpArgs(p, new FreeRdpLaunchOptions { MajorVersion = 3 }).Should().Contain("/sec:nla:off");
        RdpProtocol.BuildFreeRdpArgs(p, new FreeRdpLaunchOptions { MajorVersion = 2 }).Should().Contain("-sec-nla")
            .And.NotContain("/sec:nla:off");
    }

    [Fact]
    public void EmbeddedLaunch_PassesParentWindowAndPixelSize()
    {
        var args = RdpProtocol.BuildFreeRdpArgs(Params(), new FreeRdpLaunchOptions
        {
            ParentWindow = 0x200016,
            Size = new PixelSize(1344, 900),
        });

        args.Should().Contain("/parent-window:2097174").And.Contain("/size:1344x900").And.Contain("/dynamic-resolution");
        args.Should().NotContain(a => a.StartsWith("/title:"));
    }

    [Fact]
    public void SeparateWindowLaunch_HasTitleAndNoParent()
    {
        var args = RdpProtocol.BuildFreeRdpArgs(Params(), new FreeRdpLaunchOptions { Title = "srv - mRemoteNG" });

        args.Should().Contain("/title:srv - mRemoteNG");
        args.Should().NotContain(a => a.StartsWith("/parent-window") || a.StartsWith("/size"));
    }

    [Theory]
    [InlineData(null, 3, "/cert:tofu")]
    [InlineData("tofu", 3, "/cert:tofu")]
    [InlineData("IGNORE", 3, "/cert:ignore")]
    [InlineData("deny", 3, "/cert:deny")]
    [InlineData("bogus", 3, "/cert:tofu")]
    [InlineData("deny", 2, "/cert-deny")]
    [InlineData(null, 2, "/cert-tofu")]
    public void CertificatePolicy_MapsToFreeRdpOption(string? policy, int major, string expected)
    {
        var extras = new Dictionary<string, string>();
        if (policy is not null) extras[RdpProtocol.CertPolicyKey] = policy;

        var args = RdpProtocol.BuildFreeRdpArgs(Params(extras), new FreeRdpLaunchOptions { MajorVersion = major });

        args.Should().Contain(expected);
        args.Count(a => a.StartsWith("/cert")).Should().Be(1);
    }

    [Theory]
    [InlineData("local", "/sound")]
    [InlineData("remote", "/audio-mode:1")]
    [InlineData("off", "/audio-mode:2")]
    public void Sound_MapsToFreeRdpOption(string sound, string expected)
    {
        RdpProtocol.BuildFreeRdpArgs(Params(new() { [Keys.RdpSound] = sound })).Should().Contain(expected);
    }

    [Theory]
    [InlineData("This is FreeRDP version 3.32.1 (3.32.1)", 3)]
    [InlineData("This is FreeRDP version 3.5.1 (3.5.1)", 3)]
    [InlineData("This is FreeRDP version 2.6.1 (2.6.1+dfsg1-3ubuntu2)", 2)]
    [InlineData("garbage", 0)]
    public void ParseFreeRdpMajorVersion_ReadsMajor(string output, int expected)
    {
        RdpProtocol.ParseFreeRdpMajorVersion(output).Should().Be(expected);
    }
}
