using System.Net.Sockets;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Protocols.Telnet;

/// <summary>
/// Pure .NET Telnet (RFC 854) protocol implementation presented in a <see cref="TerminalView"/>.
///
/// Option negotiation (see <see cref="TelnetCodec"/>):
///   • Server WILL ECHO / SUPPRESS-GO-AHEAD → accepted
///   • We offer WILL NAWS and send the window size on every terminal resize
///   • All other options are refused
/// </summary>
public sealed class TelnetProtocol : ProtocolBase, IVisualProtocol
{
    private readonly ILogger<TelnetProtocol> _logger;
    private readonly TelnetCodec _codec = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private TerminalView? _view;
    private CancellationTokenSource? _cts;
    private int _columns = 80;
    private int _rows = 24;

    /// <summary>Decoded server text; lets tests observe output without a view.</summary>
    internal event Action<string>? TextReceived;

    public TelnetProtocol(ILogger<TelnetProtocol> logger) => _logger = logger;

    public Control CreateView()
    {
        _view = new TerminalView();
        _view.DataToSend += OnTerminalDataToSend;
        _view.TerminalResized += OnTerminalResized;
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
            if (_view is not null)
                (_columns, _rows) = (_view.TerminalCols, _view.TerminalRows);

            State = ConnectionState.Connected;
            RaiseStatus($"Connected to {parameters.DisplayName}");

            await WriteAsync(_codec.OfferWindowSize(), ct);

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

    /// <summary>Sends terminal input (keystrokes, paste) to the server.</summary>
    internal Task SendInputAsync(byte[] input, CancellationToken ct = default) =>
        WriteAsync(TelnetCodec.EncodeInput(input), ct);

    /// <summary>Records the terminal size and tells the server when it accepted NAWS.</summary>
    internal Task ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        (_columns, _rows) = (columns, rows);
        return _codec.WindowSizeEnabled
            ? WriteAsync(TelnetCodec.EncodeWindowSize(columns, rows), ct)
            : Task.CompletedTask;
    }

    private void OnTerminalDataToSend(object? sender, byte[] data) => _ = SendSafelyAsync(() => SendInputAsync(data));

    private void OnTerminalResized(object? sender, TerminalSizeEventArgs e) =>
        _ = SendSafelyAsync(() => ResizeAsync(e.Columns, e.Rows));

    private async Task SendSafelyAsync(Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _logger.LogDebug(ex, "Telnet write failed");
        }
    }

    /// <summary>Option replies (read loop) and keystrokes (UI thread) must not interleave on the socket.</summary>
    private async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        if (_stream is null || data.Length == 0) return;
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(data, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_stream is null) return;
        var buffer = new byte[4096];
        var negotiations = new List<TelnetNegotiation>();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await _stream.ReadAsync(buffer, ct);
                if (read == 0) break;

                negotiations.Clear();
                var text = _codec.Decode(buffer.AsSpan(0, read), negotiations);
                foreach (var negotiation in negotiations)
                    await WriteAsync(_codec.Respond(negotiation, _columns, _rows), ct);

                if (text.Length > 0)
                {
                    _view?.Write(text);
                    TextReceived?.Invoke(text);
                }
            }

            State = ConnectionState.Disconnected;
            RaiseStatus("Connection closed by the remote host.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Telnet read loop ended");
            State = ConnectionState.Error;
            RaiseStatus($"Connection lost: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_view is not null)
            {
                _view.DataToSend -= OnTerminalDataToSend;
                _view.TerminalResized -= OnTerminalResized;
            }
            _cts?.Cancel();
            _cts?.Dispose();
            _stream?.Dispose();
            _tcp?.Dispose();
            _writeLock.Dispose();
        }
    }
}
