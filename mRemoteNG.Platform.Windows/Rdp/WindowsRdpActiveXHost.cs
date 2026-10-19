using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using MSTSCLib;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Rdp;
using Keys = mRemoteNG.Protocols.Abstractions.ConnectionParametersFactory.Keys;

namespace mRemoteNG.Platform.Windows.Rdp;

/// <summary>
/// Embeds the Microsoft Remote Desktop ActiveX control (mstscax.dll, the same control the WinForms app uses)
/// in the Avalonia session tab.
///
/// The control only exists as a COM object with an HWND, so it is parented to the tab's native child window
/// (<see cref="RdpNativeHost"/>) and kept at that window's size. It paints the remote desktop itself, which is
/// why the session appears inside the tab instead of in a separate mstsc.exe window.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRdpActiveXHost : IDisposable
{
    private readonly RdpNativeHost _host;
    private readonly ConnectionParameters _parameters;
    private readonly Action<string> _warn;
    private readonly System.Windows.Forms.Panel _panel = new() { BackColor = System.Drawing.Color.Black };
    private AxHost? _ax;
    private MsRdpClient9NotSafeForScripting? _client;
    private bool _connectedRaised;
    private bool _dynamicResize;
    private int _appliedWidth;
    private int _appliedHeight;
    private System.Threading.Timer? _resizeDebounce;

    public event EventHandler? Connected;
    public event EventHandler<string>? Failed;
    public event EventHandler<string>? Disconnected;

    private WindowsRdpActiveXHost(RdpNativeHost host, ConnectionParameters parameters, Action<string> warn)
    {
        _host = host;
        _parameters = parameters;
        _warn = warn;
    }

    /// <summary>
    /// Creates the control inside <paramref name="host"/> and starts connecting. Returns null when the control
    /// is not installed or the tab has no native window, so the caller can fall back to a separate window.
    /// </summary>
    public static WindowsRdpActiveXHost? TryConnect(RdpNativeHost? host, ConnectionParameters parameters, Action<string> warn)
    {
        if (host?.NativeHandle is not { } parent || parent == 0 || host.EmbeddingFailed)
            return null;

        var session = new WindowsRdpActiveXHost(host, parameters, warn);
        try
        {
            session.Start();
            return session;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException)
        {
            session.Dispose();
            warn($"The embedded Remote Desktop control could not be created ({ex.Message}); a separate window is used instead.");
            return null;
        }
    }

    private void Start()
    {
        // The control will only activate inside a window that owns it. A borderless panel parented to the
        // tab's native window gives it one; the panel (and so the desktop) then fills the tab.
        SetParent(_panel.Handle, _host.NativeHandle);
        Resize();

        _ax = new RdpAxHost();
        _panel.Controls.Add(_ax);
        _ax.Dock = DockStyle.Fill;
        _ax.CreateControl();
        _client = (MsRdpClient9NotSafeForScripting)_ax.GetOcx()
                  ?? throw new InvalidOperationException("the Remote Desktop control did not create its window");
        var client = _client;

        Configure(client);
        client.OnConnected += OnControlConnected;
        client.OnLoginComplete += OnControlConnected;
        client.OnFatalError += OnControlFatalError;
        client.OnDisconnected += OnControlDisconnected;
        Resize();
        _host.SizeChanged += (_, _) => Resize();
        client.Connect();
    }

    private void Configure(MsRdpClient9NotSafeForScripting client)
    {
        var extras = _parameters.Extras;
        bool Flag(string key, bool fallback) =>
            extras.TryGetValue(key, out string? v) ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : fallback;
        string? Text(string key) =>
            extras.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        client.Server = _parameters.Hostname;
        client.AdvancedSettings2.RDPPort = _parameters.Port > 0 ? _parameters.Port : 3389;
        client.UserName = _parameters.Username ?? string.Empty;
        client.Domain = _parameters.Domain ?? string.Empty;
        if (!string.IsNullOrEmpty(_parameters.Password))
            ((IMsRdpClientAdvancedSettings)client.AdvancedSettings2).ClearTextPassword = _parameters.Password;

        var display = RdpDisplaySettings.From(_parameters);
        // Fit to window and full screen follow the window; a fixed resolution or smart sizing does not.
        _dynamicResize = display.AutoResize && display.Mode is RdpResolutionMode.FitToWindow or RdpResolutionMode.Fullscreen;
        ApplyDisplay(client, display);
        client.AdvancedSettings2.SmartSizing = display.Mode == RdpResolutionMode.SmartSize;
        client.ColorDepth = int.TryParse(Text(Keys.RdpColorDepth), out int depth) ? depth : 32;

        client.AdvancedSettings7.EnableCredSspSupport = Flag(Keys.RdpNla, true);
        client.AdvancedSettings5.AuthenticationLevel = (uint)(extras.GetValueOrDefault(RdpProtocol.CertPolicyKey) switch
        {
            "deny" => 1,
            "ignore" => 0,
            _ => 2,
        });
        ((IMsRdpClientAdvancedSettings3)client.AdvancedSettings3).EnableAutoReconnect = true;
        client.AdvancedSettings7.ConnectToAdministerServer = Flag(Keys.RdpConsole, false);

        var secured = client.SecuredSettings2;
        secured.KeyboardHookMode = Flag(Keys.RdpRedirectKeys, false) ? 1 : 0;
        secured.StartProgram = Text(Keys.RdpStartProgram) ?? string.Empty;
        secured.WorkDir = Text(Keys.RdpStartProgramWorkDir) ?? string.Empty;

        var advanced = client.AdvancedSettings9;
        advanced.RedirectClipboard = Flag(Keys.RdpClipboard, true);
        advanced.RedirectPrinters = Flag(Keys.RdpRedirectPrinters, false);
        advanced.RedirectPorts = Flag(Keys.RdpRedirectPorts, false);
        advanced.RedirectSmartCards = Flag(Keys.RdpRedirectSmartCards, false);
        advanced.AudioRedirectionMode = (uint)(extras.GetValueOrDefault(Keys.RdpSound, "local") switch
        {
            "local" => 0,
            "remote" => 1,
            _ => 2,
        });
        advanced.AudioCaptureRedirectionMode = Flag(Keys.RdpMicrophone, false);

        if (Text(Keys.RdpLoadBalanceInfo) is { } lb)
            advanced.LoadBalanceInfo = lb;
        if (Flag(Keys.RdpUseRedirectionServerName, false))
            ((IMsRdpPreferredRedirectionInfo)(IMsRdpClient9)client).UseRedirectionServerName = true;

        ApplyPerformance(client);
        ApplyGateway(client, extras, Text, Flag);
        if (Text(Keys.RdpVmId) is not null)
            _warn("The embedded Remote Desktop control cannot open a Hyper-V console; the host is connected as a normal RDP session.");
    }

    private void ApplyDisplay(IMsRdpClient9 client, RdpDisplaySettings display)
    {
        // Full screen uses mRemoteNG's own full-screen window (the tab moves into it) rather than the control's,
        // which would cover the whole screen including mRemoteNG. The desktop then follows that window's size.
        var (width, height) = PixelSize();
        switch (display.Mode)
        {
            case RdpResolutionMode.Fixed when display.FixedSize is { } size:
                client.DesktopWidth = size.Width;
                client.DesktopHeight = size.Height;
                break;
            default:
                client.DesktopWidth = Math.Max(200, width);
                client.DesktopHeight = Math.Max(200, height);
                break;
        }
        _appliedWidth = client.DesktopWidth;
        _appliedHeight = client.DesktopHeight;
    }

    private void ApplyPerformance(IMsRdpClient client)
    {
        var extras = _parameters.Extras;
        bool On(string key) => extras.TryGetValue(key, out string? v) && v.Equals("true", StringComparison.OrdinalIgnoreCase);
        // Same flags the WinForms app sets (TsPerfCounter). Absent keys leave the control's own defaults.
        const int wallpaper = 0x1, drag = 0x2, anims = 0x4, themes = 0x8, cursor = 0x20, fonts = 0x80, composition = 0x100;
        int flags = 0;
        if (extras.ContainsKey(Keys.RdpWallpaper) && !On(Keys.RdpWallpaper)) flags |= wallpaper;
        if (extras.ContainsKey(Keys.RdpFullWindowDrag) && !On(Keys.RdpFullWindowDrag)) flags |= drag;
        if (extras.ContainsKey(Keys.RdpMenuAnimations) && !On(Keys.RdpMenuAnimations)) flags |= anims;
        if (extras.ContainsKey(Keys.RdpThemes) && !On(Keys.RdpThemes)) flags |= themes;
        if (On(Keys.RdpDisableCursorShadow) || On(Keys.RdpDisableCursorBlinking)) flags |= cursor;
        if (On(Keys.RdpFontSmoothing)) flags &= ~fonts; else if (extras.ContainsKey(Keys.RdpFontSmoothing)) flags |= fonts;
        if (On(Keys.RdpDesktopComposition)) flags &= ~composition; else if (extras.ContainsKey(Keys.RdpDesktopComposition)) flags |= composition;
        client.AdvancedSettings2.PerformanceFlags = flags;
    }

    private void ApplyGateway(IMsRdpClient9 client, IReadOnlyDictionary<string, string> extras, Func<string, string?> text, Func<string, bool, bool> flag)
    {
        if (text(Keys.RdpGateway) is not { } gateway)
            return;
        var transport = client.TransportSettings;
        transport.GatewayHostname = gateway;
        transport.GatewayUsageMethod = (uint)(extras.GetValueOrDefault(Keys.RdpGatewayUsage) == "detect" ? 2 : 1);
        transport.GatewayProfileUsageMethod = 1;
        transport.GatewayCredsSource = (uint)(extras.GetValueOrDefault(Keys.RdpGatewayCredentials) switch
        {
            "smartcard" => 1,
            "explicit" => 4,
            "token" => 5,
            _ => 0,
        });
        if (extras.GetValueOrDefault(Keys.RdpGatewayCredentials) == "explicit")
        {
            var gatewayCreds = (IMsRdpClientTransportSettings2)transport;
            gatewayCreds.GatewayUsername = text(Keys.RdpGatewayUsername) ?? string.Empty;
            gatewayCreds.GatewayDomain = text(Keys.RdpGatewayDomain) ?? string.Empty;
            if (text(Keys.RdpGatewayPassword) is { } password)
                gatewayCreds.GatewayPassword = password;
        }
        _ = flag;
    }

    private void Resize()
    {
        var (width, height) = PixelSize();
        MoveWindow(_panel.Handle, 0, 0, width, height, true);
        if (!_dynamicResize || !_connectedRaised)
            return;
        // The window reports a size per layout pass while it is being dragged; apply the last one.
        _pendingWidth = width;
        _pendingHeight = height;
        _resizeDebounce ??= new System.Threading.Timer(_ => ApplyRemoteSize(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _resizeDebounce.Change(TimeSpan.FromMilliseconds(200), System.Threading.Timeout.InfiniteTimeSpan);
    }

    private int _pendingWidth;
    private int _pendingHeight;

    private void ApplyRemoteSize()
    {
        int width = _pendingWidth;
        int height = _pendingHeight;
        var client = _client;
        if (client is null || width < 200 || height < 200 || (width == _appliedWidth && height == _appliedHeight))
            return;
        try
        {
            uint scale = (uint)Math.Clamp((int)Math.Round(RenderScale() * 100), 100, 500);
            ((IMsRdpClient9)client).UpdateSessionDisplaySettings((uint)width, (uint)height, (uint)width, (uint)height, 0, scale, 100);
            _appliedWidth = width;
            _appliedHeight = height;
        }
        catch (COMException)
        {
            // The server does not support dynamic resolution; the desktop stays at the size it connected with.
            _dynamicResize = false;
        }
    }

    private (int Width, int Height) PixelSize()
    {
        double scale = RenderScale();
        return (Math.Max(1, (int)Math.Round(_host.Bounds.Width * scale)), Math.Max(1, (int)Math.Round(_host.Bounds.Height * scale)));
    }

    private double RenderScale() => TopLevel.GetTopLevel(_host)?.RenderScaling ?? 1.0;

    private void OnControlConnected()
    {
        if (_connectedRaised) return;
        _connectedRaised = true;
        Connected?.Invoke(this, EventArgs.Empty);
    }

    private void OnControlFatalError(int errorCode) =>
        Failed?.Invoke(this, $"The Remote Desktop control reported a fatal error ({errorCode}).");

    private void OnControlDisconnected(int reason)
    {
        // 1 and 2 are the control's own "local disconnection" and "remote logoff" codes.
        string message = reason is 1 or 2
            ? "The remote session was disconnected."
            : $"The remote session was disconnected (reason {reason}).";
        Disconnected?.Invoke(this, message);
    }

    public void Disconnect()
    {
        try { _client?.Disconnect(); }
        catch (COMException) { }
    }

    public void Dispose()
    {
        _resizeDebounce?.Dispose();
        Disconnect();
        _ax?.Dispose();
        _panel.Dispose();
        _client = null;
    }

    /// <summary>WinForms wrapper around MsRdpClient9NotSafeForScripting (mstscax.dll).</summary>
    private sealed class RdpAxHost : AxHost
    {
        public RdpAxHost() : base("8b918b82-7985-4c24-89df-c33ad2bbfbcd") { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(nint hWnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint child, nint parent);
}
