using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Protocols.Telnet;

/// <summary>
/// Pure .NET Telnet (RFC 854) protocol implementation.
/// Handles IAC option negotiation and presents a <see cref="TerminalView"/>.
///
/// Option negotiation strategy:
///   • Server WILL ECHO → we acknowledge (DONT ECHO locally)
///   • Server WILL SUPPRESS-GO-AHEAD → acknowledge
///   • We advertise WILL NAWS (window size) so the server can resize the terminal
///   • All other WILL/DO proposals from server → WONT/DONT
/// </summary>
public sealed class TelnetProtocol : ProtocolBase, IVisualProtocol
{
    private readonly ILogger<TelnetProtocol> _logger;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private TerminalView? _view;
    private CancellationTokenSource? _cts;

    // Telnet option constants
    private const byte IAC = 255;
    private const byte DONT = 254;
    private const byte DO = 253;
    private const byte WONT = 252;
    private const byte WILL = 251;
    private const byte SB = 250;   // Subnegotiation begin
    private const byte SE = 240;   // Subnegotiation end
    private const byte OPT_ECHO = 1;
    private const byte OPT_SGA = 3;   // Suppress go-ahead
    private const byte OPT_NAWS = 31; // Negotiate about window size

    public TelnetProtocol(ILogger<TelnetProtocol> logger) => _logger = logger;

    public Control CreateView()
    {
        _view = new TerminalView();
        return _view;
    }

    public override async Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        State = ConnectionState.Connecting;
        RaiseStatus($"Connecting to {parameters.DisplayName}…");
        try
        {
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(parameters.Hostname, parameters.Port, ct);
            _stream = _tcp.GetStream();

            State = ConnectionState.Connected;
            RaiseStatus($"Connected to {parameters.DisplayName}");

            // Send WILL NAWS immediately
            await SendOptionAsync(WILL, OPT_NAWS, ct);
            await SendNawsAsync(_view?.TerminalCols ?? 80, _view?.TerminalRows ?? 24, ct);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = ReadLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            State = ConnectionState.Error;
            RaiseStatus($"Connection failed: {ex.Message}");
            _logger.LogError(ex, "Telnet connect failed");
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
        var output = new StringBuilder();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await _stream.ReadAsync(buffer, ct);
                if (read == 0) break;

                output.Clear();
                int i = 0;
                while (i < read)
                {
                    byte b = buffer[i++];
                    if (b == IAC && i < read)
                    {
                        byte cmd = buffer[i++];
                        if ((cmd == WILL || cmd == WONT || cmd == DO || cmd == DONT) && i < read)
                        {
                            byte opt = buffer[i++];
                            await HandleOptionAsync(cmd, opt, ct);
                        }
                        else if (cmd == SB)
                        {
                            // Skip subnegotiation bytes until IAC SE
                            while (i < read - 1 && !(buffer[i] == IAC && buffer[i + 1] == SE))
                                i++;
                            i += 2; // skip IAC SE
                        }
                        // IAC IAC = literal 0xFF
                        else if (cmd == IAC) output.Append((char)IAC);
                    }
                    else
                    {
                        output.Append((char)b);
                    }
                }

                if (output.Length > 0)
                    _view?.Write(output.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telnet read loop ended");
            State = ConnectionState.Error;
        }
    }

    private async Task HandleOptionAsync(byte cmd, byte opt, CancellationToken ct)
    {
        switch (cmd)
        {
            case WILL when opt == OPT_ECHO:
                await SendOptionAsync(DO, OPT_ECHO, ct); break;
            case WILL when opt == OPT_SGA:
                await SendOptionAsync(DO, OPT_SGA, ct); break;
            case DO when opt == OPT_NAWS:
                await SendNawsAsync(_view?.TerminalCols ?? 80, _view?.TerminalRows ?? 24, ct); break;
            case WILL:
                await SendOptionAsync(DONT, opt, ct); break;
            case DO:
                await SendOptionAsync(WONT, opt, ct); break;
        }
    }

    private async Task SendOptionAsync(byte cmd, byte opt, CancellationToken ct)
    {
        if (_stream is null) return;
        byte[] data = [IAC, cmd, opt];
        await _stream.WriteAsync(data, ct);
    }

    private async Task SendNawsAsync(int cols, int rows, CancellationToken ct)
    {
        if (_stream is null) return;
        byte colHi = (byte)(cols >> 8), colLo = (byte)(cols & 0xFF);
        byte rowHi = (byte)(rows >> 8), rowLo = (byte)(rows & 0xFF);
        byte[] data = [IAC, SB, OPT_NAWS, colHi, colLo, rowHi, rowLo, IAC, SE];
        await _stream.WriteAsync(data, ct);
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
