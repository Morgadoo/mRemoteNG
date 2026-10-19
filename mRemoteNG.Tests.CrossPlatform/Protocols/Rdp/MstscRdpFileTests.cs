using FluentAssertions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Rdp;
using Xunit;
using Keys = mRemoteNG.Protocols.Abstractions.ConnectionParametersFactory.Keys;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Rdp;

/// <summary>The Windows Remote Desktop fallback: connection settings become an .rdp file mstsc can open.</summary>
public class MstscRdpFileTests
{
    private static ConnectionParameters Params(Dictionary<string, string>? extras = null) => new()
    {
        Hostname = "server.example",
        Port = 3389,
        Protocol = ProtocolType.Rdp,
        Username = "alice",
        Domain = "ERSE",
        Password = "s3cret",
        Extras = extras ?? new Dictionary<string, string>(),
    };

    private static string FileFor(ConnectionParameters p, MstscLaunchOptions? options = null) =>
        MstscRdpFile.Build(p, options ?? new MstscLaunchOptions { ProtectPassword = _ => null });

    private static string Value(string file, string name)
    {
        string prefix = name + ":";
        return file.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Single(l => l.StartsWith(prefix, StringComparison.Ordinal))[(prefix.Length + 2)..];
    }

    [Fact]
    public void Defaults_TargetTheHost_AndLetWindowsNegotiateSecurity()
    {
        string file = FileFor(Params());

        Value(file, "full address").Should().Be("server.example");
        Value(file, "server port").Should().Be("3389");
        Value(file, "username").Should().Be("alice");
        Value(file, "domain").Should().Be("ERSE");
        Value(file, "enablecredsspsupport").Should().Be("1");
        Value(file, "redirectclipboard").Should().Be("1");
        Value(file, "audiomode").Should().Be("0");
        file.Should().NotContain("password 51");
        file.Should().NotContain("drivestoredirect");
    }

    [Fact]
    public void NonDefaultPort_IsPartOfTheAddress()
    {
        var p = Params();
        p = p with { Port = 3390 };

        Value(FileFor(p), "full address").Should().Be("server.example:3390");
    }

    [Fact]
    public void Password_IsStoredOnlyAsAProtectedBlob()
    {
        string file = MstscRdpFile.Build(Params(), new MstscLaunchOptions { ProtectPassword = s => [(byte)s.Length] });

        Value(file, "password 51").Should().Be("06");
        file.Should().NotContain("s3cret");
    }

    [Fact]
    public void UnprotectedPassword_IsLeftOut_SoWindowsAsksForIt()
    {
        var options = new MstscLaunchOptions { ProtectPassword = _ => null };

        string file = MstscRdpFile.Build(Params(), options);

        file.Should().NotContain("password 51");
        options.Warnings.Should().Contain(w => w.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CredSspOff_DisablesCredSsp()
    {
        string file = FileFor(Params(new() { [Keys.RdpNla] = "false" }));

        Value(file, "enablecredsspsupport").Should().Be("0");
    }

    [Theory]
    [InlineData("ignore", "0")]
    [InlineData("tofu", "2")]
    [InlineData("deny", "1")]
    public void CertificatePolicy_BecomesMstscAuthenticationLevel(string policy, string level)
    {
        string file = FileFor(Params(new() { [RdpProtocol.CertPolicyKey] = policy }));

        Value(file, "authentication level").Should().Be(level);
    }

    [Fact]
    public void Fullscreen_UsesScreenMode2_AndRemoteKeysOnlyWhileFullscreen()
    {
        string file = FileFor(Params(new() { [Keys.RdpResolution] = "fullscreen" }));

        Value(file, "screen mode id").Should().Be("2");
        Value(file, "keyboardhook").Should().Be("2");
    }

    [Fact]
    public void FixedResolution_SetsTheDesktopSize()
    {
        string file = FileFor(Params(new() { [Keys.RdpResolution] = "1280x1024" }));

        Value(file, "screen mode id").Should().Be("1");
        Value(file, "desktopwidth").Should().Be("1280");
        Value(file, "desktopheight").Should().Be("1024");
        Value(file, "smart sizing").Should().Be("0");
        Value(file, "dynamic resolution").Should().Be("0");
    }

    [Fact]
    public void RedirectKeys_SendsCombinationsWhileTheSessionIsFocused()
    {
        string file = FileFor(Params(new() { [Keys.RdpRedirectKeys] = "true" }));

        Value(file, "keyboardhook").Should().Be("1");
    }

    [Fact]
    public void CustomDrives_BecomeMstscDriveList()
    {
        string file = FileFor(Params(new()
        {
            [Keys.RdpDrives] = "custom",
            [Keys.RdpDrivesCustom] = "C,D,home=/srv/share",
        }));

        Value(file, "drivestoredirect").Should().Be(@"C:\;D:\;");
    }

    [Fact]
    public void LineBreaksInValues_CannotSmuggleExtraSettings()
    {
        var p = Params() with { Username = "alice\r\nredirectdrives:i:1" };

        FileFor(p).Should().Contain("username:s:aliceredirectdrives:i:1");
    }

    [Fact]
    public void Gateway_UsesItsOwnCredentials()
    {
        string file = FileFor(Params(new()
        {
            [Keys.RdpGateway] = "gw.example",
            [Keys.RdpGatewayUsage] = "detect",
            [Keys.RdpGatewayCredentials] = "explicit",
            [Keys.RdpGatewayUsername] = "gwuser",
            [Keys.RdpGatewayDomain] = "GW",
        }));

        Value(file, "gatewayhostname").Should().Be("gw.example");
        Value(file, "gatewayusagemethod").Should().Be("2");
        Value(file, "gatewaycredentialssource").Should().Be("4");
        Value(file, "gatewayusername").Should().Be("gwuser");
        Value(file, "promptcredentialonce").Should().Be("0");
    }

    [Fact]
    public void RestrictedAdminAndRemoteGuard_AreSwitches_AndSendNoPassword()
    {
        var restricted = Params(new() { [Keys.RdpRestrictedAdmin] = "true", [Keys.RdpConsole] = "true" });
        var guard = Params(new() { [Keys.RdpRemoteCredentialGuard] = "true" });

        MstscRdpFile.Build(restricted).Should().NotContain("password 51").And.NotContain("s3cret");
        MstscRdpFile.Arguments(restricted).Should().Equal("/admin", "/restrictedAdmin");
        MstscRdpFile.Arguments(guard).Should().Equal("/remoteGuard");
        MstscRdpFile.Arguments(Params()).Should().BeEmpty();
    }
}
