using System.Net.Sockets;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Vnc.Rfb;
using Keys = mRemoteNG.Protocols.Abstractions.ConnectionParametersFactory.Keys;

namespace mRemoteNG.Protocols.Vnc;

/// <summary>How the remote desktop is fitted into the session tab.</summary>
public enum VncScaling
{
    /// <summary>1:1 pixels, with scroll bars when the desktop is larger than the tab.</summary>
    None,

    /// <summary>Scaled to fit, keeping the aspect ratio.</summary>
    Fit,

    /// <summary>Scaled to fill the tab, ignoring the aspect ratio.</summary>
    Stretch,
}

/// <summary>
/// VNC (RFB) session backed by the managed <see cref="RfbClient"/>; rendered by <see cref="VncView"/>.
/// Connection extras (see <see cref="VncSessionSettings"/>): scaling, view-only, preferred encoding, compression and
/// JPEG levels, colour depth, authentication mode and proxy. A session can also run over a connection the server
/// opened to us (UltraVNC SingleClick / reverse VNC): see <see cref="UseIncomingConnection"/>.
/// </summary>
public sealed class VncProtocol : ProtocolBase, IVisualProtocol, ISpecialKeysProtocol, IDisplayOptionsProtocol, IRefreshableProtocol
{
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogger<VncProtocol> _logger;
    private VncView? _view;
    private TcpClient? _tcp;
    private TcpClient? _incoming;
    private volatile RfbClient? _client;
    private volatile bool _closing;
    private volatile bool _viewOnly;
    private VncScaling _scaledMode = VncScaling.Fit;

    public VncProtocol(ILogger<VncProtocol> logger) => _logger = logger;

    public VncScaling Scaling { get; private set; } = VncScaling.Fit;

    /// <summary>Input is not sent to the server; can be toggled while connected.</summary>
    public bool ViewOnly
    {
        get => _viewOnly;
        set
        {
            if (_viewOnly == value) return;
            _view?.BeforeViewOnlyChange(value);
            _viewOnly = value;
            RaiseStatus(value ? "View only: input is not sent to the remote desktop." : "Input is sent to the remote desktop.");
        }
    }

    /// <summary>The settings the current (or last) connection was made with.</summary>
    public VncSessionSettings Settings { get; private set; } = new();

    /// <summary>The remote framebuffer, or null before the session is established.</summary>
    public Framebuffer? Framebuffer => _client?.Framebuffer;

    /// <summary>Name the server reported in ServerInit.</summary>
    public string? DesktopName => _client?.DesktopName;

    /// <summary>The security type the server and client agreed on (see <see cref="RfbSecurityType"/>).</summary>
    public byte? SecurityType => _client?.SecurityType;

    /// <summary>Rectangles received per RFB encoding, for diagnostics.</summary>
    public IReadOnlyDictionary<int, long> RectangleCounts => _client?.RectangleCounts ?? new Dictionary<int, long>();

    /// <summary>Raised on the receive thread when the server publishes clipboard text.</summary>
    public event EventHandler<string>? ServerClipboardChanged;

    /// <summary>
    /// Runs the next <see cref="ConnectAsync"/> over a connection the VNC server opened to us (reverse connection,
    /// UltraVNC SingleClick) instead of dialling out. The protocol takes ownership of <paramref name="client"/>;
    /// the parameters' host and port are then only used for display.
    /// </summary>
    public void UseIncomingConnection(TcpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (_client is not null)
            throw new InvalidOperationException("This VNC session is already connected.");
        _incoming?.Dispose();
        _incoming = client;
    }

    // ── IVisualProtocol ────────────────────────────────────────────────────

    public Control CreateView()
    {
        _view ??= new VncView(this);
        return _view;
    }

    // ── IProtocol ──────────────────────────────────────────────────────────

    public override async Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (_client is not null)
            throw new InvalidOperationException("This VNC session is already connected.");

        Settings = VncSessionSettings.FromParameters(parameters);
        Scaling = Settings.Scaling;
        if (Scaling != VncScaling.None) _scaledMode = Scaling;
        _viewOnly = Settings.ViewOnly;
        _closing = false;
        _view?.ApplySettings();

        var incoming = _incoming;
        _incoming = null;
        var target = incoming is null ? parameters.DisplayName : $"{parameters.DisplayName} (incoming)";
        State = ConnectionState.Connecting;
        RaiseStatus($"Connecting VNC to {target}…");
        _view?.ShowStatus($"Connecting to {target}…");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout(parameters));
        try
        {
            _tcp = incoming ?? await VncTransport.ConnectAsync(parameters.Hostname, parameters.Port, Settings.Proxy, timeout.Token).ConfigureAwait(false);
            _tcp.NoDelay = true;

            var options = new RfbClientOptions
            {
                Username = VncSessionSettings.AccountName(parameters, Settings.AuthMode),
                Password = parameters.Password,
                Encodings = Settings.Encodings,
                PixelFormat = Settings.PixelFormat,
                SecurityTypes = Settings.SecurityTypes,
            };
            var client = await RfbClient.ConnectAsync(_tcp.GetStream(), options, timeout.Token).ConfigureAwait(false);
            client.FramebufferUpdated += (_, _) => _view?.OnFramebufferUpdated();
            client.CursorChanged += (_, cursor) => _view?.OnCursorChanged(cursor);
            client.ServerCutTextReceived += OnServerCutText;
            client.BellReceived += (_, _) => _logger.LogDebug("VNC bell from {Host}", parameters.Hostname);
            client.Disconnected += OnClientDisconnected;
            _client = client;

            State = ConnectionState.Connected;
            RaiseStatus($"VNC connected to {target}: \"{client.DesktopName}\" "
                + $"{client.Framebuffer.Width}×{client.Framebuffer.Height}, RFB {client.ProtocolVersion}");
            _logger.LogInformation(
                "VNC connected to {Target} ({Name}, {Width}x{Height}, RFB {Version}, {Security}, server format {Format}, client format {ClientFormat}, proxy {Proxy})",
                target, client.DesktopName, client.Framebuffer.Width, client.Framebuffer.Height, client.ProtocolVersion,
                RfbClient.SecurityTypeName(client.SecurityType), client.ServerPixelFormat, client.PixelFormat,
                Settings.Proxy?.Kind ?? VncProxyKind.None);

            _view?.OnConnected();
            client.Start();
        }
        catch (Exception ex)
        {
            CloseTransport();
            var message = ex switch
            {
                OperationCanceledException when !ct.IsCancellationRequested =>
                    $"Timed out connecting to VNC server {target}.",
                OperationCanceledException => "VNC connection cancelled.",
                SocketException se => $"Could not connect to VNC server {target}: {se.Message}",
                _ => ex.Message,
            };
            _logger.LogWarning(ex, "VNC connection to {Target} failed", target);
            State = ConnectionState.Error;
            RaiseStatus(message);
            _view?.ShowStatus(message);

            if (ex is RfbProtocolException || (ex is OperationCanceledException && ct.IsCancellationRequested))
                throw;
            throw new IOException(message, ex);
        }
    }

    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        _closing = true;
        var client = _client;
        CloseTransport();
        if (client is not null)
        {
            try { await client.Completion.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (TimeoutException) { _logger.LogWarning("VNC receive loop did not stop in time"); }
        }
        State = ConnectionState.Disconnected;
        _view?.ShowStatus("Disconnected");
    }

    private static TimeSpan ConnectTimeout(ConnectionParameters parameters) =>
        int.TryParse(parameters.Extras.GetValueOrDefault(Keys.ConnectTimeoutSeconds), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultConnectTimeout;

    // ── ISpecialKeysProtocol ───────────────────────────────────────────────

    public IReadOnlyList<SpecialKey> SupportedSpecialKeys { get; } = [SpecialKey.CtrlAltDel, SpecialKey.CtrlEsc];

    /// <summary>Presses the combination's keys in order and releases them in reverse (ignored in view-only mode).</summary>
    public Task SendSpecialKeyAsync(SpecialKey key, CancellationToken ct = default)
    {
        uint[] keys = key switch
        {
            SpecialKey.CtrlAltDel => [X11KeySymbols.ControlL, X11KeySymbols.AltL, X11KeySymbols.Delete],
            SpecialKey.CtrlEsc => [X11KeySymbols.ControlL, X11KeySymbols.Escape],
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
        };
        if (!AcceptsInput) return Task.CompletedTask;
        foreach (var keysym in keys)
            _client?.SendKeyEvent(keysym, down: true);
        for (var i = keys.Length - 1; i >= 0; i--)
            _client?.SendKeyEvent(keys[i], down: false);
        return Task.CompletedTask;
    }

    // ── IDisplayOptionsProtocol ────────────────────────────────────────────

    public bool SupportsSmartSize => true;

    /// <summary>Scale the desktop to the tab (keeping the configured fit or stretch mode) or show it 1:1.</summary>
    public bool SmartSize
    {
        get => Scaling != VncScaling.None;
        set
        {
            if (value == SmartSize) return;
            Scaling = value ? _scaledMode : VncScaling.None;
            _view?.ApplySettings();
        }
    }

    public bool SupportsViewOnly => true;

    // ── IRefreshableProtocol ───────────────────────────────────────────────

    public Task RefreshScreenAsync(CancellationToken ct = default)
    {
        if (State == ConnectionState.Connected)
            _client?.RequestUpdate(incremental: false);
        return Task.CompletedTask;
    }

    // ── Input (called by the view on the UI thread) ────────────────────────

    internal bool AcceptsInput => !_viewOnly && State == ConnectionState.Connected && _client is not null;

    internal void SendKey(uint keysym, bool down)
    {
        if (AcceptsInput) _client?.SendKeyEvent(keysym, down);
    }

    /// <summary>Sends a key event even in view-only mode; used to release keys when switching to view-only.</summary>
    internal void ReleaseKey(uint keysym)
    {
        if (State == ConnectionState.Connected) _client?.SendKeyEvent(keysym, false);
    }

    /// <summary>Releases all pointer buttons, even in view-only mode.</summary>
    internal void ReleaseButtons(int x, int y)
    {
        if (State == ConnectionState.Connected) _client?.SendPointerEvent(x, y, RfbButtons.None);
    }

    internal void SendPointer(int x, int y, RfbButtons buttons)
    {
        if (AcceptsInput) _client?.SendPointerEvent(x, y, buttons);
    }

    /// <summary>Sends local clipboard text to the server (ignored in view-only mode).</summary>
    public void SendClipboardText(string text)
    {
        if (AcceptsInput) _client?.SendClientCutText(text);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    internal static VncScaling ParseScaling(string? value) => value?.ToLowerInvariant() switch
    {
        "none" => VncScaling.None,
        "stretch" => VncScaling.Stretch,
        _ => VncScaling.Fit,
    };

    private void OnServerCutText(object? sender, string text)
    {
        ServerClipboardChanged?.Invoke(this, text);
        _view?.OnServerClipboard(text);
    }

    private void OnClientDisconnected(object? sender, Exception? error)
    {
        if (_closing || error is null) return;

        var message = error is EndOfStreamException
            ? "The VNC server closed the connection."
            : $"VNC connection lost: {error.Message}";
        _logger.LogWarning(error, "VNC session ended");
        State = ConnectionState.Error;
        RaiseStatus(message);
        _view?.ShowStatus(message);
    }

    private void CloseTransport()
    {
        var client = _client;
        _client = null;
        client?.Dispose();
        _tcp?.Dispose();
        _tcp = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        _closing = true;
        CloseTransport();
        _incoming?.Dispose();
        _incoming = null;
    }
}
