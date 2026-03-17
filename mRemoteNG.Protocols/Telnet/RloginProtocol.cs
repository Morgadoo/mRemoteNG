using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Protocols.Telnet;

/// <summary>
/// Rlogin (RFC 1282) protocol implementation.
///
/// Handshake:
///   1. Send null byte (0x00) as first byte
///   2. Send: &lt;client-username&gt;\0&lt;server-username&gt;\0&lt;terminal-type/speed&gt;\0
///   3. Wait for 0x00 acknowledgment from server
///   4. Bidirectional data flow begins
/// </summary>
public sealed class RloginProtocol : ProtocolBase, IVisualProtocol
{
    private readonly ILogger<RloginProtocol> _logger;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private TerminalView? _view;
    private CancellationTokenSource? _cts;

    public RloginProtocol(ILogger<RloginProtocol> logger) => _logger = logger;

    public Control CreateView()
    {
        _view = new TerminalView();
        return _view;
    }

    public override async Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        State = ConnectionState.Connecting;
        RaiseStatus($"Connecting (rlogin) to {parameters.DisplayName}…");
        try
        {
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(parameters.Hostname, parameters.Port, ct);
            _stream = _tcp.GetStream();

            // RFC 1282 handshake
            string clientUser = Environment.UserName;
            string serverUser = parameters.Username ?? clientUser;
            string termSpec = "xterm-256color/38400";

            var handshake = new List<byte>();
            handshake.Add(0x00); // null prefix byte
            handshake.AddRange(Encoding.ASCII.GetBytes(clientUser)); handshake.Add(0);
            handshake.AddRange(Encoding.ASCII.GetBytes(serverUser)); handshake.Add(0);
            handshake.AddRange(Encoding.ASCII.GetBytes(termSpec)); handshake.Add(0);

            await _stream.WriteAsync(handshake.ToArray(), ct);

            // Wait for server acknowledgment
            int ack = _stream.ReadByte();
            if (ack != 0)
            {
                throw new InvalidOperationException($"Rlogin handshake failed (expected 0x00, got 0x{ack:X2})");
            }

            State = ConnectionState.Connected;
            RaiseStatus($"Connected (rlogin) to {parameters.DisplayName}");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = ReadLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            State = ConnectionState.Error;
            RaiseStatus($"Rlogin failed: {ex.Message}");
            _logger.LogError(ex, "Rlogin connect failed");
            throw;
        }
    }

    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        _stream?.Close();
        _tcp?.Close();
        State = ConnectionState.Disconnected;
        await Task.CompletedTask;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_stream is null) return;
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await _stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                string text = Encoding.UTF8.GetString(buffer, 0, read);
                _view?.Write(text);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rlogin read loop ended");
            State = ConnectionState.Error;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _stream?.Dispose();
            _tcp?.Dispose();
        }
    }
}
