using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>
/// SSH protocol implementation using SSH.NET (Renci.SshNet).
/// Replaces the legacy PuTTYNG.exe-based SSH handler.
///
/// Supports:
///   • Host key verification against known_hosts (<see cref="IHostKeyVerifier"/>)
///   • Public-key, password and keyboard-interactive authentication
///   • An interactive xterm-256color shell in a <see cref="TerminalView"/>, resized with the view
///   • The connection's opening command, sent once the shell is open
///   • Keep-alive messages every 30 seconds
/// </summary>
public sealed class SshNetProtocol : ProtocolBase, IVisualProtocol
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<SshNetProtocol> _logger;
    private readonly SshConnector _connector;
    private SshClient? _client;
    private ShellStream? _shellStream;
    private CancellationTokenSource? _readCts;
    private TerminalView? _terminalView;

    public SshNetProtocol(ILogger<SshNetProtocol> logger, IHostKeyVerifier hostKeyVerifier, ISshUserPrompt userPrompt)
    {
        _logger = logger;
        _connector = new SshConnector(hostKeyVerifier, userPrompt, logger);
    }

    // ── IVisualProtocol ────────────────────────────────────────────────────

    public Avalonia.Controls.Control CreateView()
    {
        _terminalView = new TerminalView();
        return _terminalView;
    }

    // ── IProtocol ──────────────────────────────────────────────────────────

    public override async Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        State = ConnectionState.Connecting;
        RaiseStatus($"Connecting to {parameters.DisplayName}…");
        WriteNotice($"Connecting to {parameters.DisplayName}…", "90");

        try
        {
            _client = await _connector.ConnectAsync(parameters, info => new SshClient(info), ct);
            _client.ErrorOccurred += OnErrorOccurred;
            _client.KeepAliveInterval = KeepAliveInterval;

            int cols = _terminalView?.TerminalCols ?? 80;
            int rows = _terminalView?.TerminalRows ?? 24;
            _shellStream = _client.CreateShellStream("xterm-256color", (uint)cols, (uint)rows, 0, 0, 32 * 1024);

            if (_terminalView is not null)
            {
                _terminalView.DataToSend += OnTerminalDataToSend;
                _terminalView.TerminalResized += OnTerminalResized;
                // The view may have been laid out while we were connecting.
                if (_terminalView.TerminalCols != cols || _terminalView.TerminalRows != rows)
                    ResizeShell(_terminalView.TerminalCols, _terminalView.TerminalRows, 0, 0);
            }

            State = ConnectionState.Connected;
            RaiseStatus($"Connected to {parameters.Hostname}");

            _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = Task.Run(() => ReadLoop(_readCts.Token), CancellationToken.None);

            SendOpeningCommand(parameters);
        }
        catch (Exception ex)
        {
            _client?.Dispose();
            _client = null;

            var message = ex switch
            {
                HostKeyVerificationException => $"Host key verification failed: {ex.Message}",
                SshAuthenticationException => $"Authentication failed: {ex.Message}",
                OperationCanceledException => "Connection cancelled.",
                _ => $"Connection failed: {ex.Message}",
            };
            _logger.LogError(ex, "SSH connection to {Host} failed", parameters.DisplayName);
            WriteNotice(message, ex is HostKeyVerificationException ? "1;97;41" : "1;31");
            State = ConnectionState.Error;
            RaiseStatus(message);
            throw;
        }
    }

    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        _readCts?.Cancel();
        DetachView();

        if (_shellStream is not null)
        {
            _shellStream.Dispose();
            _shellStream = null;
        }

        if (_client is not null)
        {
            var client = _client;
            _client = null;
            await Task.Run(() =>
            {
                try { client.Disconnect(); }
                catch (Exception ex) { _logger.LogDebug(ex, "SSH disconnect failed"); }
                client.Dispose();
            }, ct);
        }

        State = ConnectionState.Disconnected;
        RaiseStatus("Disconnected.");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>Sends <see cref="ConnectionParametersFactory.Keys.OpeningCommand"/> line by line.</summary>
    private void SendOpeningCommand(ConnectionParameters parameters)
    {
        if (!parameters.Extras.TryGetValue(ConnectionParametersFactory.Keys.OpeningCommand, out var command)
            || string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        var lines = command.Replace("\r\n", "\n").Split('\n');
        var payload = string.Concat(lines.Select(line => line + "\r"));
        SendToShell(Encoding.UTF8.GetBytes(payload));
        _logger.LogDebug("Sent opening command to {Host}", parameters.DisplayName);
    }

    private void ReadLoop(CancellationToken ct)
    {
        var stream = _shellStream;
        if (stream is null) return;

        var decoder = Encoding.UTF8.GetDecoder();
        var buffer = new byte[32 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                    break; // channel closed (e.g. the user typed "exit")

                int charCount = decoder.GetChars(buffer, 0, bytesRead, chars, 0);
                _terminalView?.Write(new string(chars, 0, charCount));
            }

            if (!ct.IsCancellationRequested)
            {
                WriteNotice("Session closed by the remote host.", "90");
                State = ConnectionState.Disconnected;
                RaiseStatus("Session closed by the remote host.");
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException && ct.IsCancellationRequested)
        {
            // Disconnect in progress.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSH read loop ended unexpectedly");
            WriteNotice($"Connection lost: {ex.Message}", "1;31");
            State = ConnectionState.Error;
            RaiseStatus($"Connection lost: {ex.Message}");
        }
    }

    private void OnTerminalDataToSend(object? sender, byte[] data) => SendToShell(data);

    private void SendToShell(byte[] data)
    {
        try
        {
            _shellStream?.Write(data, 0, data.Length);
            _shellStream?.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send data to SSH shell");
        }
    }

    private void OnTerminalResized(object? sender, TerminalSizeEventArgs e) =>
        ResizeShell(e.Columns, e.Rows, e.PixelWidth, e.PixelHeight);

    private void ResizeShell(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        try
        {
            _shellStream?.ChangeWindowSize((uint)columns, (uint)rows, (uint)pixelWidth, (uint)pixelHeight);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send window-change to SSH shell");
        }
    }

    /// <summary>Prints a local status line in the terminal using the given SGR attributes.</summary>
    private void WriteNotice(string message, string sgr) =>
        _terminalView?.Write($"\r\n\x1b[{sgr}m{message}\x1b[0m\r\n");

    private void OnErrorOccurred(object? sender, ExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "SSH client error");
        State = ConnectionState.Error;
        RaiseStatus($"Error: {e.Exception.Message}");
    }

    private void DetachView()
    {
        if (_terminalView is null)
            return;
        _terminalView.DataToSend -= OnTerminalDataToSend;
        _terminalView.TerminalResized -= OnTerminalResized;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DetachView();
            _readCts?.Cancel();
            _readCts?.Dispose();
            _shellStream?.Dispose();
            _client?.Dispose();
        }
    }
}
