using System.Buffers.Binary;
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

/// <summary>Special keys, display toggles, refresh and the VNC connection settings.</summary>
public sealed class VncProtocolCapabilityTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly FakeRfbServer _server = new();
    private readonly VncProtocol _protocol = new(NullLogger<VncProtocol>.Instance);
    private byte[] _setPixelFormat = [];
    private int[] _encodings = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _protocol.Dispose();
        await _server.DisposeAsync();
    }

    private async Task ConnectAsync(Dictionary<string, string>? extras = null)
    {
        var connect = _protocol.ConnectAsync(new ConnectionParameters
        {
            Hostname = "127.0.0.1",
            Port = _server.Port,
            Protocol = ProtocolType.Vnc,
            Extras = extras ?? new Dictionary<string, string>(),
        });
        await _server.AcceptAsync();
        await _server.NegotiateVersionAsync("003.008");
        await _server.SendAsync(new RfbBytes().U8(1).U8(RfbSecurityType.None));
        await _server.ReceiveAsync(1);
        await _server.SendAsync(new RfbBytes().U32(0));
        (_, _setPixelFormat, _encodings) = await _server.InitialiseAsync(64, 48, "caps");
        await connect.WaitAsync(Timeout);
        // The first full update request from Start().
        (await _server.ReceiveAsync(10))[0].Should().Be(3);
    }

    private async Task<List<(bool Down, uint Keysym)>> ReceiveKeyEventsAsync(int count)
    {
        var events = new List<(bool, uint)>();
        for (var i = 0; i < count; i++)
        {
            var message = await _server.ReceiveAsync(8);
            message[0].Should().Be(4, "a KeyEvent");
            events.Add((message[1] == 1, BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(4))));
        }
        return events;
    }

    [Fact]
    public async Task CtrlAltDel_PressesInOrder_AndReleasesInReverse()
    {
        await ConnectAsync();
        ISpecialKeysProtocol keys = _protocol;
        keys.SupportedSpecialKeys.Should().Equal(SpecialKey.CtrlAltDel, SpecialKey.CtrlEsc);

        await keys.SendSpecialKeyAsync(SpecialKey.CtrlAltDel);
        await keys.SendSpecialKeyAsync(SpecialKey.CtrlEsc);

        (await ReceiveKeyEventsAsync(10)).Should().Equal(
            (true, X11KeySymbols.ControlL), (true, X11KeySymbols.AltL), (true, X11KeySymbols.Delete),
            (false, X11KeySymbols.Delete), (false, X11KeySymbols.AltL), (false, X11KeySymbols.ControlL),
            (true, X11KeySymbols.ControlL), (true, X11KeySymbols.Escape),
            (false, X11KeySymbols.Escape), (false, X11KeySymbols.ControlL));
    }

    [Fact]
    public async Task ViewOnly_CanBeToggledWhileConnected_AndBlocksInput()
    {
        await ConnectAsync();
        IDisplayOptionsProtocol display = _protocol;
        display.SupportsViewOnly.Should().BeTrue();

        display.ViewOnly = true;
        await _protocol.SendSpecialKeyAsync(SpecialKey.CtrlEsc);
        _protocol.SendClipboardText("ignored");
        display.ViewOnly = false;
        await _protocol.SendSpecialKeyAsync(SpecialKey.CtrlEsc);

        // Only the second Ctrl+Esc reaches the server.
        (await ReceiveKeyEventsAsync(4)).Should().Equal(
            (true, X11KeySymbols.ControlL), (true, X11KeySymbols.Escape),
            (false, X11KeySymbols.Escape), (false, X11KeySymbols.ControlL));
    }

    [Fact]
    public async Task SmartSize_TogglesBetweenOneToOneAndTheConfiguredScaling()
    {
        await ConnectAsync(new Dictionary<string, string> { [Keys.VncScaling] = "stretch" });
        IDisplayOptionsProtocol display = _protocol;

        display.SupportsSmartSize.Should().BeTrue();
        display.SmartSize.Should().BeTrue();
        display.SmartSize = false;
        _protocol.Scaling.Should().Be(VncScaling.None);
        display.SmartSize = true;
        _protocol.Scaling.Should().Be(VncScaling.Stretch, "turning smart size back on restores the stretch mode");
    }

    [Fact]
    public async Task RefreshScreen_RequestsANonIncrementalFullUpdate()
    {
        await ConnectAsync();

        await ((IRefreshableProtocol)_protocol).RefreshScreenAsync();

        var request = await _server.ReceiveAsync(10);
        request[0].Should().Be(3, "FramebufferUpdateRequest");
        request[1].Should().Be(0, "not incremental");
        BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(6)).Should().Be(64);
        BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(8)).Should().Be(48);
    }

    [Fact]
    public async Task Settings_ChooseEncodingOrder_CompressionAndPixelFormat()
    {
        await ConnectAsync(new Dictionary<string, string>
        {
            [Keys.VncEncoding] = "hextile",
            [Keys.VncCompression] = "3",
            [Keys.VncJpegQuality] = "7",
            [Keys.VncColors] = "8",
        });

        _encodings.Take(2).Should().Equal(RfbEncoding.CopyRect, RfbEncoding.Hextile);
        _encodings.Should().Contain(RfbEncoding.Tight).And.Contain(RfbEncoding.Raw);
        _encodings.Should().Contain(RfbEncoding.CompressLevel0 + 3).And.Contain(RfbEncoding.QualityLevel0 + 7);
        _setPixelFormat[4].Should().Be(8, "8 bits per pixel");
        _setPixelFormat[7].Should().Be(1, "true colour");
    }

    [Fact]
    public async Task Settings_DefaultToFullColourLosslessWithoutLevels()
    {
        await ConnectAsync();

        _encodings.Should().NotContain(e => e >= RfbEncoding.CompressLevel0 && e < RfbEncoding.CompressLevel0 + 10);
        _encodings.Should().NotContain(e => e >= RfbEncoding.QualityLevel0 && e < RfbEncoding.QualityLevel0 + 10);
        _setPixelFormat[4].Should().Be(32);
    }

    // ── Connection settings mapping ────────────────────────────────────────

    [Fact]
    public void ConnectionParametersFactory_MapsEveryVncOption()
    {
        var info = new ConnectionInfo
        {
            Protocol = CoreProtocol.VNC,
            Hostname = "h",
            VNCEncoding = VncEncoding.EncZRLE,
            VNCCompression = VncCompression.Comp9,
            VNCColors = VncColors.Col8Bit,
            VNCAuthMode = VncAuthMode.AuthWin,
            VNCProxyType = VncProxyType.ProxySocks5,
            VNCProxyIP = "proxy.lan",
            VNCProxyPort = 1081,
            VNCProxyUsername = "pu",
            VNCProxyPassword = "pp",
        };

        var parameters = ConnectionParametersFactory.FromConnectionInfo(info);
        var settings = VncSessionSettings.FromParameters(parameters);

        parameters.Extras[Keys.VncEncoding].Should().Be("zrle");
        settings.PreferredEncoding.Should().Be(RfbEncoding.Zrle);
        settings.CompressionLevel.Should().Be(9);
        settings.JpegQuality.Should().BeNull();
        settings.ColourDepth.Should().Be(VncColourDepth.Low);
        settings.PixelFormat.Should().Be(PixelFormat.Bgr233);
        settings.AuthMode.Should().Be(VncAuthenticationMode.Windows);
        settings.SecurityTypes[0].Should().Be(RfbSecurityType.MsLogon2);
        settings.Proxy.Should().Be(new VncProxySettings(VncProxyKind.Socks5, "proxy.lan", 1081, "pu", "pp"));
    }

    [Fact]
    public void ConnectionParametersFactory_NoCompressionAndNoProxy_AddNothing()
    {
        var info = new ConnectionInfo { Protocol = CoreProtocol.VNC, Hostname = "h", VNCCompression = VncCompression.CompNone };

        var extras = ConnectionParametersFactory.FromConnectionInfo(info).Extras;

        extras.Should().NotContainKey(Keys.VncCompression);
        extras.Should().NotContainKey(Keys.VncProxyType);
        VncSessionSettings.FromParameters(ConnectionParametersFactory.FromConnectionInfo(info)).Proxy.Should().BeNull();
    }

    [Fact]
    public void AppleRemoteDesktopConnections_PreferArdAuthentication()
    {
        var info = new ConnectionInfo { Protocol = CoreProtocol.ARD, Hostname = "mac", Username = "alice", Password = "pw" };

        var parameters = ConnectionParametersFactory.FromConnectionInfo(info);
        var settings = VncSessionSettings.FromParameters(parameters);

        parameters.Protocol.Should().Be(ProtocolType.Vnc);
        settings.AuthMode.Should().Be(VncAuthenticationMode.AppleRemoteDesktop);
        settings.SecurityTypes[0].Should().Be(RfbSecurityType.AppleRemoteDesktop);
    }

    [Fact]
    public void MsLogon_UsesDomainBackslashUser()
    {
        var parameters = new ConnectionParameters
        {
            Hostname = "h",
            Port = 5900,
            Protocol = ProtocolType.Vnc,
            Username = "bob",
            Domain = "CORP",
        };

        VncSessionSettings.AccountName(parameters, VncAuthenticationMode.Windows).Should().Be(@"CORP\bob");
        VncSessionSettings.AccountName(parameters, VncAuthenticationMode.Vnc).Should().Be("bob");
    }

    [Theory]
    [InlineData("http", null, VncProxyKind.Http, 8080)]
    [InlineData("socks5", "0", VncProxyKind.Socks5, 1080)]
    [InlineData("ultravnc", "5555", VncProxyKind.UltraVncRepeater, 5555)]
    public void ProxyPort_DefaultsPerKind(string type, string? port, VncProxyKind kind, int expectedPort)
    {
        var extras = new Dictionary<string, string> { [Keys.VncProxyType] = type, [Keys.VncProxyHost] = "p" };
        if (port is not null) extras[Keys.VncProxyPort] = port;

        var settings = VncSessionSettings.FromParameters(new ConnectionParameters
        {
            Hostname = "h",
            Port = 5900,
            Protocol = ProtocolType.Vnc,
            Extras = extras,
        });

        settings.Proxy!.Kind.Should().Be(kind);
        settings.Proxy.Port.Should().Be(expectedPort);
    }
}
