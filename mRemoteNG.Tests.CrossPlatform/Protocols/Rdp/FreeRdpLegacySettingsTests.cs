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

/// <summary>
/// The legacy RDP connection settings (mstsc ActiveX semantics) and the FreeRDP 3 options they become,
/// both through <see cref="ConnectionParametersFactory"/> and directly through the Extras keys.
/// </summary>
public class FreeRdpLegacySettingsTests
{
    private static readonly PixelSize Tab = new(1300, 860);

    private static ConnectionParameters Params(Dictionary<string, string>? extras = null) => new()
    {
        Hostname = "srv",
        Port = 3389,
        Protocol = ProtocolType.Rdp,
        Username = "alice",
        Password = "pw",
        Extras = extras ?? new Dictionary<string, string>(),
    };

    private static FreeRdpLaunchOptions Embedded(int major = 3) => new() { MajorVersion = major, ParentWindow = 0x400001, Size = Tab };

    private static FreeRdpLaunchOptions Separate(bool windows = false) => new() { Title = "srv - mRemoteNG", WindowsClient = windows };

    private static IReadOnlyList<string> Args(Dictionary<string, string> extras, FreeRdpLaunchOptions? options = null) =>
        RdpProtocol.BuildFreeRdpArgs(Params(extras), options ?? Separate());

    private static IReadOnlyList<string> ArgsFor(ConnectionInfo info, FreeRdpLaunchOptions? options = null) =>
        RdpProtocol.BuildFreeRdpArgs(ConnectionParametersFactory.FromConnectionInfo(info), options ?? Separate());

    private static ConnectionInfo Rdp() => new()
    {
        Protocol = CoreProtocol.RDP,
        Hostname = "srv",
        Username = "alice",
        Password = "pw",
        AutomaticResize = true,
        Colors = RDPColors.Colors32Bit,
    };

    // ── Resolution ─────────────────────────────────────────────────────────

    [Fact]
    public void FitToWindow_WithAutomaticResize_Embedded_FollowsTabWithDynamicResolution()
    {
        var args = ArgsFor(Rdp(), Embedded());

        args.Should().Contain("/size:1300x860").And.Contain("/dynamic-resolution");
        args.Should().NotContain("/smart-sizing").And.NotContain("/f");
    }

    [Fact]
    public void FitToWindow_WithoutAutomaticResize_Embedded_KeepsTabSizeAndCanSmartSize()
    {
        var info = Rdp();
        info.AutomaticResize = false;

        var args = ArgsFor(info, Embedded());

        args.Should().Contain("/size:1300x860").And.Contain("/smart-sizing").And.NotContain("/dynamic-resolution");
    }

    [Fact]
    public void SmartSize_Embedded_StartsAtTabSizeWithSmartSizing()
    {
        var info = Rdp();
        info.Resolution = RDPResolutions.SmartSize;

        var args = ArgsFor(info, Embedded());

        args.Should().Contain("/size:1300x860").And.Contain("/smart-sizing").And.NotContain("/dynamic-resolution");
    }

    [Theory]
    [InlineData(RDPResolutions.Res800x600, "/size:800x600")]
    [InlineData(RDPResolutions.Res1024x768, "/size:1024x768")]
    [InlineData(RDPResolutions.Res1920x1080, "/size:1920x1080")]
    [InlineData(RDPResolutions.Res3840x2160, "/size:3840x2160")]
    public void FixedResolution_Embedded_UsesThatSizeNotTheTab(RDPResolutions resolution, string expected)
    {
        var info = Rdp();
        info.Resolution = resolution;

        var args = ArgsFor(info, Embedded());

        args.Should().Contain(expected).And.Contain("/smart-sizing");
        args.Should().NotContain("/size:1300x860").And.NotContain("/dynamic-resolution");
        args.Count(a => a.StartsWith("/size:")).Should().Be(1);
    }

    [Fact]
    public void FixedResolution_SeparateWindow_NoScalingNoResize()
    {
        var info = Rdp();
        info.Resolution = RDPResolutions.Res1280x1024;

        var args = ArgsFor(info);

        args.Should().Contain("/size:1280x1024").And.Contain("/title:srv - mRemoteNG");
        args.Should().NotContain("/smart-sizing").And.NotContain("/dynamic-resolution");
    }

    [Fact]
    public void SmartSize_SeparateWindow_ScalesFreeRdpWindow()
    {
        var args = Args(new() { [Keys.RdpResolution] = "smartsize" });

        args.Should().Contain("/smart-sizing").And.NotContain("/dynamic-resolution");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Fullscreen_IsNeverEmbedded(bool autoResize)
    {
        var info = Rdp();
        info.Resolution = RDPResolutions.Fullscreen;
        info.AutomaticResize = autoResize;

        var args = ArgsFor(info, Embedded());

        args.Should().Contain("/f");
        args.Should().NotContain(a => a.StartsWith("/parent-window") || a.StartsWith("/size"));
        args.Contains("/dynamic-resolution").Should().Be(autoResize);
        args.Should().NotContain("/smart-sizing");
    }

    [Theory]
    [InlineData("fit", "true")]
    [InlineData("fit", "false")]
    [InlineData("smartsize", "true")]
    [InlineData("fullscreen", "true")]
    [InlineData("1024x768", "true")]
    public void SmartSizingAndDynamicResolution_AreNeverCombined(string resolution, string autoResize)
    {
        // FreeRDP rejects the combination ("mutually exclusive options").
        var extras = new Dictionary<string, string> { [Keys.RdpResolution] = resolution, [Keys.RdpAutoResize] = autoResize };

        foreach (var options in new[] { Embedded(), Separate() })
        {
            var args = Args(extras, options);
            (args.Contains("/smart-sizing") && args.Contains("/dynamic-resolution")).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("10x10")]
    [InlineData("99999x768")]
    public void InvalidResolution_FallsBackToFitToWindow(string value)
    {
        var args = Args(new() { [Keys.RdpResolution] = value }, Embedded());

        args.Should().Contain("/size:1300x860").And.Contain("/dynamic-resolution");
    }

    [Theory]
    [InlineData(RDPResolutions.FitToWindow, "fit")]
    [InlineData(RDPResolutions.SmartSize, "smartsize")]
    [InlineData(RDPResolutions.Fullscreen, "fullscreen")]
    [InlineData(RDPResolutions.Res2560x1440, "2560x1440")]
    public void Factory_MapsResolution(RDPResolutions resolution, string expected)
    {
        var info = Rdp();
        info.Resolution = resolution;

        ConnectionParametersFactory.FromConnectionInfo(info).Extras[Keys.RdpResolution].Should().Be(expected);
    }

    [Fact]
    public void DisplaySettings_DescribeEmbeddedLayout()
    {
        var fixedSize = RdpDisplaySettings.From(Params(new() { [Keys.RdpResolution] = "1024x768" }));
        fixedSize.EmbeddedDesktopSize(Tab).Should().Be(new PixelSize(1024, 768));
        fixedSize.StartsScaled.Should().BeFalse();
        fixedSize.CanEmbed.Should().BeTrue();

        var smart = RdpDisplaySettings.From(Params(new() { [Keys.RdpResolution] = "smartsize" }));
        smart.EmbeddedDesktopSize(Tab).Should().Be(Tab);
        smart.StartsScaled.Should().BeTrue();

        RdpDisplaySettings.From(Params(new() { [Keys.RdpResolution] = "fullscreen" })).CanEmbed.Should().BeFalse();
        RdpDisplaySettings.From(Params()).Should().Be(RdpDisplaySettings.Default);
    }

    // ── Colour depth and experience ────────────────────────────────────────

    [Fact]
    public void ThirtyTwoBit_LeavesFreeRdpAutoDetectDefaults()
    {
        // /network, /gfx, /rfx or /bpp would disable FreeRDP's auto-detect defaults, and /network:auto
        // would override the experience options because FreeRDP parses it after them.
        var args = ArgsFor(Rdp());

        args.Should().NotContain(a => a.StartsWith("/bpp") || a.StartsWith("/network"));
    }

    [Theory]
    [InlineData(RDPColors.Colors256, "/bpp:8")]
    [InlineData(RDPColors.Colors15Bit, "/bpp:15")]
    [InlineData(RDPColors.Colors16Bit, "/bpp:16")]
    [InlineData(RDPColors.Colors24Bit, "/bpp:24")]
    public void LowerColourDepths_PassBpp(RDPColors colors, string expected)
    {
        var info = Rdp();
        info.Colors = colors;

        var args = ArgsFor(info);

        args.Should().Contain(expected).And.NotContain(a => a.StartsWith("/network"));
    }

    [Fact]
    public void FreeRdp2_KeepsNetworkAutoAndBpp()
    {
        var args = Args(new(), new FreeRdpLaunchOptions { MajorVersion = 2 });

        args.Should().Contain("/network:auto").And.Contain("/bpp:32");
    }

    [Fact]
    public void ExperienceOptions_Enabled_MapToPlusToggles()
    {
        var info = Rdp();
        info.DisplayWallpaper = true;
        info.DisplayThemes = true;
        info.EnableFontSmoothing = true;
        info.EnableDesktopComposition = true;
        info.DisableFullWindowDrag = false;
        info.DisableMenuAnimations = false;

        var args = ArgsFor(info);

        args.Should().Contain(["+wallpaper", "+themes", "+fonts", "+aero", "+window-drag", "+menu-anims"]);
    }

    [Fact]
    public void ExperienceOptions_Disabled_MapToMinusToggles()
    {
        var info = Rdp();
        info.DisplayWallpaper = false;
        info.DisplayThemes = false;
        info.EnableFontSmoothing = false;
        info.EnableDesktopComposition = false;
        info.DisableFullWindowDrag = true;
        info.DisableMenuAnimations = true;

        var args = ArgsFor(info);

        args.Should().Contain(["-wallpaper", "-themes", "-fonts", "-aero", "-window-drag", "-menu-anims"]);
    }

    [Fact]
    public void ExperienceOptions_Absent_LeaveFreeRdpDefaults()
    {
        var args = Args(new());

        args.Should().NotContain(a => a.EndsWith("wallpaper") || a.EndsWith("themes") || a.EndsWith("fonts")
                                      || a.EndsWith("aero") || a.EndsWith("window-drag") || a.EndsWith("menu-anims"));
    }

    [Theory]
    [InlineData(true, 3, "/cache:persist")]
    [InlineData(true, 2, "+persist-cache")]
    public void CacheBitmaps_EnablesPersistentBitmapCache(bool cache, int major, string expected)
    {
        var info = Rdp();
        info.CacheBitmaps = cache;

        ArgsFor(info, new FreeRdpLaunchOptions { MajorVersion = major }).Should().Contain(expected);
    }

    [Fact]
    public void CacheBitmapsOff_AddsNothing()
    {
        ArgsFor(Rdp()).Should().NotContain(a => a.Contains("persist"));
    }

    // ── Keyboard ───────────────────────────────────────────────────────────

    [Fact]
    public void RedirectKeysOff_DoesNotGrabTheKeyboard()
    {
        var info = Rdp();
        info.RedirectKeys = false;

        ArgsFor(info, Embedded()).Should().Contain("-grab-keyboard");
    }

    [Fact]
    public void RedirectKeysOn_KeepsFreeRdpKeyboardGrab()
    {
        var info = Rdp();
        info.RedirectKeys = true;

        ArgsFor(info, Embedded()).Should().NotContain(a => a.Contains("grab-keyboard"));
    }

    [Fact]
    public void RedirectKeysOff_FullScreen_StillGrabs()
    {
        // mstsc's default: Windows key combinations go to the remote computer in full screen.
        var info = Rdp();
        info.RedirectKeys = false;
        info.Resolution = RDPResolutions.Fullscreen;

        ArgsFor(info).Should().NotContain("-grab-keyboard");
    }

    // ── Device redirection ─────────────────────────────────────────────────

    [Fact]
    public void RedirectPorts_NamesEachLocalPort()
    {
        var info = Rdp();
        info.RedirectPorts = true;
        var devices = new RdpLocalDevices
        {
            SerialPorts = [new("COM1", "/dev/ttyS0"), new("COM2", "/dev/ttyUSB0")],
            ParallelPorts = [new("LPT1", "/dev/lp0")],
        };

        var args = ArgsFor(info, new FreeRdpLaunchOptions { LocalDevices = devices });

        args.Should().Contain(["/serial:COM1,/dev/ttyS0", "/serial:COM2,/dev/ttyUSB0", "/parallel:LPT1,/dev/lp0"]);
    }

    [Fact]
    public void RedirectPorts_WithoutLocalPorts_Warns()
    {
        var info = Rdp();
        info.RedirectPorts = true;
        var warnings = new List<string>();

        var args = ArgsFor(info, new FreeRdpLaunchOptions { Warn = warnings.Add });

        args.Should().NotContain(a => a.StartsWith("/serial") || a.StartsWith("/parallel"));
        warnings.Should().ContainSingle().Which.Should().Contain("no local serial or parallel port");
    }

    [Fact]
    public void RedirectPrintersAndSmartCards()
    {
        var info = Rdp();
        info.RedirectPrinters = true;
        info.RedirectSmartCards = true;

        ArgsFor(info).Should().Contain("/printer").And.Contain("/smartcard");

        ArgsFor(Rdp()).Should().NotContain(a => a.StartsWith("/printer") || a.StartsWith("/smartcard") || a.StartsWith("/serial"));
    }

    [Fact]
    public void Drives_None_RedirectsNothing()
    {
        ArgsFor(Rdp()).Should().NotContain(a => a.StartsWith("/drive") || a.EndsWith("drives") || a.EndsWith("home-drive"));
    }

    [Fact]
    public void Drives_Local_OnLinuxOrMac_IsTheHomeFolder()
    {
        var info = Rdp();
        info.RedirectDiskDrives = RDPDiskDrives.Local;

        ArgsFor(info, Separate(windows: false)).Should().Contain("+home-drive").And.NotContain("+drives");
    }

    [Fact]
    public void Drives_Local_OnWindows_AreTheFixedDrives()
    {
        var info = Rdp();
        info.RedirectDiskDrives = RDPDiskDrives.Local;
        var options = Separate(windows: true) with
        {
            LocalDevices = new RdpLocalDevices { FixedDrives = [new("C", "C:/"), new("D", "D:/")] },
        };

        var args = ArgsFor(info, options);

        args.Should().Contain(["/drive:C,C:/", "/drive:D,D:/"]).And.NotContain("+home-drive");
    }

    [Fact]
    public void Drives_All_RedirectsEveryMountPoint()
    {
        var info = Rdp();
        info.RedirectDiskDrives = RDPDiskDrives.All;

        ArgsFor(info).Should().Contain("+drives");
    }

    [Fact]
    public void Drives_Custom_Paths_GetOneShareEach()
    {
        var info = Rdp();
        info.RedirectDiskDrives = RDPDiskDrives.Custom;
        info.RedirectDiskDrivesCustom = "/srv/share; ~/Documents ; Data=/mnt/data disk, /tmp/share";
        var options = Separate() with { HomeDirectory = "/home/alice" };

        var args = ArgsFor(info, options);

        args.Where(a => a.StartsWith("/drive:")).Should().Equal(
            "/drive:share,/srv/share",
            "/drive:Documents,/home/alice/Documents",
            "/drive:Data,/mnt/data disk",
            "/drive:share2,/tmp/share");
    }

    [Fact]
    public void Drives_Custom_LegacyLetters_OnWindows()
    {
        var info = Rdp();
        info.RedirectDiskDrives = RDPDiskDrives.Custom;
        info.RedirectDiskDrivesCustom = "C,d:,X";

        var args = ArgsFor(info, Separate(windows: true));

        args.Where(a => a.StartsWith("/drive:")).Should().Equal("/drive:C,C:/", "/drive:D,D:/", "/drive:X,X:/");
    }

    [Fact]
    public void Drives_Custom_LettersOnLinux_AndQuotedPaths_AreSkippedWithWarnings()
    {
        var info = Rdp();
        info.RedirectDiskDrives = RDPDiskDrives.Custom;
        info.RedirectDiskDrivesCustom = "C;/home/o'brien;/ok";
        var warnings = new List<string>();

        var args = ArgsFor(info, Separate() with { Warn = warnings.Add });

        args.Where(a => a.StartsWith("/drive:")).Should().Equal("/drive:ok,/ok");
        warnings.Should().HaveCount(2);
    }

    [Fact]
    public void Drives_LegacyHomeDriveKey_StillHonoured()
    {
        Args(new() { [Keys.RdpHomeDrive] = "true" }).Should().Contain("+home-drive");
    }

    [Theory]
    [InlineData(RDPSoundQuality.Dynamic, "/sound:quality:dynamic")]
    [InlineData(RDPSoundQuality.Medium, "/sound:quality:medium")]
    [InlineData(RDPSoundQuality.High, "/sound:quality:high")]
    public void SoundQuality_IsPassedToTheAudioChannel(RDPSoundQuality quality, string expected)
    {
        var info = Rdp();
        info.RedirectSound = RDPSounds.BringToThisComputer;
        info.SoundQuality = quality;

        ArgsFor(info).Should().Contain(expected).And.NotContain("/sound");
    }

    [Fact]
    public void AudioCapture_RedirectsMicrophone()
    {
        var info = Rdp();
        info.RedirectAudioCapture = true;
        info.RedirectSound = RDPSounds.LeaveAtRemoteComputer;

        ArgsFor(info).Should().Contain("/microphone").And.Contain("/audio-mode:1").And.NotContain(a => a.StartsWith("/sound"));
    }

    // ── Security ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AuthenticationLevel.NoAuth, "/cert:ignore")]
    [InlineData(AuthenticationLevel.WarnOnFailedAuth, "/cert:tofu")]
    [InlineData(AuthenticationLevel.AuthRequired, "/cert:deny")]
    public void AuthenticationLevel_DecidesCertificatePolicy(AuthenticationLevel level, string expected)
    {
        var info = Rdp();
        info.RDPAuthenticationLevel = level;

        ArgsFor(info).Should().Contain(expected);
    }

    [Fact]
    public void RestrictedAdmin_KeepsNlaAndWinsOverRemoteCredentialGuard()
    {
        var info = Rdp();
        info.UseRestrictedAdmin = true;
        info.UseRCG = true;
        info.UseCredSsp = false;

        var args = ArgsFor(info);

        args.Should().Contain("+restricted-admin");
        args.Should().NotContain("-credentials-delegation").And.NotContain("/sec:nla:off");
    }

    [Fact]
    public void RemoteCredentialGuard_DoesNotDelegateCredentials_AndWarns()
    {
        var info = Rdp();
        info.UseRCG = true;
        var warnings = new List<string>();

        var args = ArgsFor(info, Separate() with { Warn = warnings.Add });

        args.Should().Contain("-credentials-delegation").And.NotContain("+restricted-admin");
        warnings.Should().ContainSingle().Which.Should().Contain("Remote Credential Guard");
    }

    // ── Start program ──────────────────────────────────────────────────────

    [Fact]
    public void StartProgram_IsTheAlternateShell()
    {
        var info = Rdp();
        info.RDPStartProgram = @"C:\Tools\app.exe /flag ""quoted arg""";
        info.RDPStartProgramWorkDir = @"C:\Tools";

        var args = ArgsFor(info);

        args.Should().Contain(@"/shell:C:\Tools\app.exe /flag ""quoted arg""").And.Contain(@"/shell-dir:C:\Tools");
    }

    [Fact]
    public void StartProgramWorkDir_WithoutProgram_IsIgnored()
    {
        var info = Rdp();
        info.RDPStartProgramWorkDir = "/tmp";

        ArgsFor(info).Should().NotContain(a => a.StartsWith("/shell"));
    }

    // ── RD Gateway ─────────────────────────────────────────────────────────

    private static ConnectionInfo WithGateway(RDGatewayUseConnectionCredentials credentials)
    {
        var info = Rdp();
        info.RDGatewayUsageMethod = RDGatewayUsageMethod.Always;
        info.RDGatewayHostname = "gw.example:8443";
        info.RDGatewayUseConnectionCredentials = credentials;
        info.RDGatewayUsername = "gwuser";
        info.RDGatewayDomain = "GWDOM";
        info.RDGatewayPassword = "gw,pa\\ss'word";
        info.RDGatewayAccessToken = "token-value";
        return info;
    }

    [Fact]
    public void Gateway_ConnectionCredentials_LetsFreeRdpReuseThem()
    {
        ArgsFor(WithGateway(RDGatewayUseConnectionCredentials.Yes)).Should().Contain("/gateway:g:gw.example:8443");
    }

    [Fact]
    public void Gateway_SeparateCredentials_PassesEscapedUserDomainPassword()
    {
        var args = ArgsFor(WithGateway(RDGatewayUseConnectionCredentials.No));

        args.Should().Contain(@"/gateway:g:gw.example:8443,u:gwuser,d:GWDOM,p:gw\,pa\\ss\'word");
    }

    [Fact]
    public void Gateway_AccessToken()
    {
        ArgsFor(WithGateway(RDGatewayUseConnectionCredentials.AccessToken))
            .Should().Contain("/gateway:g:gw.example:8443,access-token:token-value");
    }

    [Fact]
    public void Gateway_SmartCard_PassesNoSecrets()
    {
        ArgsFor(WithGateway(RDGatewayUseConnectionCredentials.SmartCard)).Should().Contain("/gateway:g:gw.example:8443");
    }

    [Fact]
    public void Gateway_Detect_UsesDetectUsageMethod()
    {
        var info = WithGateway(RDGatewayUseConnectionCredentials.Yes);
        info.RDGatewayUsageMethod = RDGatewayUsageMethod.Detect;

        ArgsFor(info).Should().Contain("/gateway:g:gw.example:8443,usage-method:detect");
    }

    [Fact]
    public void Gateway_Never_AddsNoGateway()
    {
        var info = WithGateway(RDGatewayUseConnectionCredentials.No);
        info.RDGatewayUsageMethod = RDGatewayUsageMethod.Never;

        ArgsFor(info).Should().NotContain(a => a.StartsWith("/gateway"));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", @"a\,b")]
    [InlineData(@"DOM\user", @"DOM\\user")]
    [InlineData("\"q\" 'q'", "\\\"q\\\" \\'q\\'")]
    public void GatewayValues_AreEscapedForFreeRdpListParser(string value, string expected)
    {
        RdpProtocol.EscapeGatewayValue(value).Should().Be(expected);
    }

    [Fact]
    public void RedactArgs_HidesConnectionAndGatewaySecrets()
    {
        var args = ArgsFor(WithGateway(RDGatewayUseConnectionCredentials.No));
        var tokenArgs = ArgsFor(WithGateway(RDGatewayUseConnectionCredentials.AccessToken));

        string redacted = RdpProtocol.RedactArgs(args) + " " + RdpProtocol.RedactArgs(tokenArgs);

        redacted.Should().NotContain("pw ").And.NotContain("pa\\").And.NotContain("word").And.NotContain("token-value");
        redacted.Should().Contain("/p:********").And.Contain("p:********").And.Contain("access-token:********");
        redacted.Should().Contain("u:gwuser");
    }

    // ── Hyper-V console ────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, "/vmconnect:5A0A1BB5-ED4C-4C24-9F39-3F1D2E7A3C11")]
    [InlineData(true, "/vmconnect:5A0A1BB5-ED4C-4C24-9F39-3F1D2E7A3C11;EnhancedMode=1")]
    public void VmId_ConnectsToTheVmConsole(bool enhanced, string expected)
    {
        var info = Rdp();
        info.UseVmId = true;
        info.VmId = "5A0A1BB5-ED4C-4C24-9F39-3F1D2E7A3C11";
        info.UseEnhancedMode = enhanced;
        info.UseCredSsp = false;

        var args = ArgsFor(info);

        args.Should().Contain(expected);
        args.Should().Contain("/spn-class:Microsoft Virtual Console Service").And.Contain("/sec:nla").And.Contain("-credentials-delegation");
        args.Should().NotContain("/sec:nla:off");
    }

    [Fact]
    public void VmId_WithoutUseVmId_IsIgnored()
    {
        var info = Rdp();
        info.VmId = "5A0A1BB5-ED4C-4C24-9F39-3F1D2E7A3C11";

        ArgsFor(info).Should().NotContain(a => a.StartsWith("/vmconnect"));
    }

    // ── Idle timeout (runtime) ─────────────────────────────────────────────

    [Fact]
    public void Factory_MapsIdleTimeoutOnlyWhenSet()
    {
        var info = Rdp();
        ConnectionParametersFactory.FromConnectionInfo(info).Extras.Should().NotContainKey(Keys.RdpIdleTimeoutMinutes);

        info.RDPMinutesToIdleTimeout = 15;
        info.RDPAlertIdleTimeout = true;
        var extras = ConnectionParametersFactory.FromConnectionInfo(info).Extras;

        extras[Keys.RdpIdleTimeoutMinutes].Should().Be("15");
        extras[Keys.RdpIdleTimeoutAlert].Should().Be("true");
    }

    [Fact]
    public void IdleTimeout_ElapsesWithoutInputInTheSession()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var timeout = new RdpIdleTimeout(TimeSpan.FromMinutes(5), start);

        // The user types in the session 30 s in.
        timeout.Observe(start.AddMinutes(1), userIdle: TimeSpan.FromSeconds(30), sessionHasInput: true).Should().BeFalse();
        // Then works elsewhere: desktop input does not count for the session.
        timeout.Observe(start.AddMinutes(4), userIdle: TimeSpan.Zero, sessionHasInput: false).Should().BeFalse();
        timeout.Observe(start.AddMinutes(5).AddSeconds(29), userIdle: TimeSpan.Zero, sessionHasInput: false).Should().BeFalse();
        timeout.Observe(start.AddMinutes(5).AddSeconds(30), userIdle: TimeSpan.Zero, sessionHasInput: false).Should().BeTrue();
    }

    [Fact]
    public void IdleTimeout_FocusWithoutRecentInput_IsStillIdle()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var timeout = new RdpIdleTimeout(TimeSpan.FromMinutes(1), start);

        timeout.Observe(start.AddSeconds(70), userIdle: TimeSpan.FromSeconds(70), sessionHasInput: true).Should().BeTrue();
    }

    [Fact]
    public void IdleTimeout_UnknownIdleTime_NeverElapses()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var timeout = new RdpIdleTimeout(TimeSpan.FromMinutes(1), start);

        timeout.Observe(start.AddHours(1), userIdle: null, sessionHasInput: false).Should().BeFalse();
    }

    // ── Capabilities ───────────────────────────────────────────────────────

    [Fact]
    public void Capabilities_BeforeConnecting()
    {
        using var protocol = new RdpProtocol(Microsoft.Extensions.Logging.Abstractions.NullLogger<RdpProtocol>.Instance);

        ISpecialKeysProtocol keys = protocol;
        IDisplayOptionsProtocol display = protocol;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            keys.SupportedSpecialKeys.Should().BeEquivalentTo([SpecialKey.CtrlAltDel, SpecialKey.CtrlEsc]);
        display.SupportsSmartSize.Should().BeFalse("only a running embedded session can switch smart sizing");
        display.SupportsViewOnly.Should().BeFalse();
        display.SmartSize = true;
        display.SmartSize.Should().BeFalse();
    }

    [Fact]
    public async Task SendSpecialKey_WhenNotConnected_ReportsInsteadOfThrowing()
    {
        using var protocol = new RdpProtocol(Microsoft.Extensions.Logging.Abstractions.NullLogger<RdpProtocol>.Instance);
        var messages = new List<string>();
        protocol.StatusMessage += (_, m) => messages.Add(m);

        await protocol.SendSpecialKeyAsync(SpecialKey.CtrlAltDel);

        messages.Should().ContainSingle().Which.Should().Contain("connected RDP session");
    }

    // ── Factory round-up ───────────────────────────────────────────────────

    [Fact]
    public void Factory_MapsEveryRedirectionAndSecurityFlag()
    {
        var info = Rdp();
        info.RedirectKeys = true;
        info.RedirectPorts = true;
        info.RedirectPrinters = true;
        info.RedirectSmartCards = true;
        info.RedirectDiskDrives = RDPDiskDrives.Custom;
        info.RedirectDiskDrivesCustom = "C,D";
        info.UseRestrictedAdmin = true;
        info.UseRCG = true;
        info.RDPStartProgram = "notepad.exe";
        info.RDPStartProgramWorkDir = @"C:\";
        info.CacheBitmaps = true;

        var extras = ConnectionParametersFactory.FromConnectionInfo(info).Extras;

        extras[Keys.RdpRedirectKeys].Should().Be("true");
        extras[Keys.RdpRedirectPorts].Should().Be("true");
        extras[Keys.RdpRedirectPrinters].Should().Be("true");
        extras[Keys.RdpRedirectSmartCards].Should().Be("true");
        extras[Keys.RdpDrives].Should().Be("custom");
        extras[Keys.RdpDrivesCustom].Should().Be("C,D");
        extras[Keys.RdpRestrictedAdmin].Should().Be("true");
        extras[Keys.RdpRemoteCredentialGuard].Should().Be("true");
        extras[Keys.RdpStartProgram].Should().Be("notepad.exe");
        extras[Keys.RdpStartProgramWorkDir].Should().Be(@"C:\");
        extras[Keys.RdpPersistentBitmapCache].Should().Be("true");
        extras[Keys.RdpFullWindowDrag].Should().Be("true");
        extras[Keys.RdpMenuAnimations].Should().Be("true");
    }

    [Fact]
    public void Factory_UsesInheritedRdpSettings()
    {
        var folder = new mRemoteNG.Core.Container.ContainerInfo
        {
            Name = "folder",
            Resolution = RDPResolutions.Res1024x768,
            RedirectPrinters = true,
            RDPStartProgram = "inherited.exe",
        };
        var info = Rdp();
        folder.AddChild(info);
        info.Inheritance.Resolution = true;
        info.Inheritance.RedirectPrinters = true;
        info.Inheritance.RDPStartProgram = true;

        var args = ArgsFor(info);

        args.Should().Contain("/size:1024x768").And.Contain("/printer").And.Contain("/shell:inherited.exe");
    }
}
