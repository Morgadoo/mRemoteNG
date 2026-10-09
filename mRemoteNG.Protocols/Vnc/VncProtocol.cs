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
/// Connection extras: <see cref="Keys.VncScaling"/> (none / fit / stretch) and <see cref="Keys.VncViewOnly"/>.
/// </summary>
public sealed class VncProtocol : ProtocolBase, IVisualProtocol
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    private readonly ILogger<VncProtocol> _logger;
    private VncView? _view;
    private TcpClient? _tcp;
    private volatile RfbClient? _client;
    private volatile bool _closing;

    public VncProtocol(ILogger<VncProtocol> logger) => _logger = logger;

    public VncScaling Scaling { get; private set; } = VncScaling.Fit;
    public bool ViewOnly { get; private set; }

    /// <summary>The remote framebuffer, or null before the session is established.</summary>
    public Framebuffer? Framebuffer => _client?.Framebuffer;

    /// <summary>Name the server reported in ServerInit.</summary>
    public string? DesktopName => _client?.DesktopName;

    /// <summary>Raised on the receive thread when the server publishes clipboard text.</summary>
    public event EventHandler<string>? ServerClipboardChanged;

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

        Scaling = ParseScaling(parameters.Extras.GetValueOrDefault(Keys.VncScaling));
        ViewOnly = string.Equals(parameters.Extras.GetValueOrDefault(Keys.VncViewOnly), "true", StringComparison.OrdinalIgnoreCase);
        _closing = false;
        _view?.ApplySettings();

        State = ConnectionState.Connecting;
        RaiseStatus($"Connecting VNC to {parameters.DisplayName}…");
        _view?.ShowStatus($"Connecting to {parameters.DisplayName}…");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            _tcp = new TcpClient { NoDelay = true };
            await _tcp.ConnectAsync(parameters.Hostname, parameters.Port, timeout.Token).ConfigureAwait(false);

            var options = new RfbClientOptions { Password = parameters.Password };
            var client = await RfbClient.ConnectAsync(_tcp.GetStream(), options, timeout.Token).ConfigureAwait(false);
            client.FramebufferUpdated += (_, _) => _view?.OnFramebufferUpdated();
            client.CursorChanged += (_, cursor) => _view?.OnCursorChanged(cursor);
            client.ServerCutTextReceived += OnServerCutText;
            client.BellReceived += (_, _) => _logger.LogDebug("VNC bell from {Host}", parameters.Hostname);
            client.Disconnected += OnClientDisconnected;
            _client = client;

            State = ConnectionState.Connected;
            RaiseStatus($"VNC connected to {parameters.DisplayName}: \"{client.DesktopName}\" "
                + $"{client.Framebuffer.Width}×{client.Framebuffer.Height}, RFB {client.ProtocolVersion}");
            _logger.LogInformation("VNC connected to {Host}:{Port} ({Name}, {Width}x{Height}, RFB {Version}, server format {Format})",
                parameters.Hostname, parameters.Port, client.DesktopName, client.Framebuffer.Width,
                client.Framebuffer.Height, client.ProtocolVersion, client.ServerPixelFormat);

            _view?.OnConnected();
            client.Start();
        }
        catch (Exception ex)
        {
            CloseTransport();
            var message = ex switch
            {
                OperationCanceledException when !ct.IsCancellationRequested =>
                    $"Timed out connecting to VNC server {parameters.DisplayName}.",
                OperationCanceledException => "VNC connection cancelled.",
                SocketException se => $"Could not connect to VNC server {parameters.DisplayName}: {se.Message}",
                _ => ex.Message,
            };
            _logger.LogWarning(ex, "VNC connection to {Host}:{Port} failed", parameters.Hostname, parameters.Port);
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

    // ── Input (called by the view on the UI thread) ────────────────────────

    internal bool AcceptsInput => !ViewOnly && State == ConnectionState.Connected && _client is not null;

    internal void SendKey(uint keysym, bool down)
    {
        if (AcceptsInput) _client?.SendKeyEvent(keysym, down);
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
    }
}
