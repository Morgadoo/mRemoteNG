using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Protocols.Telnet;

/// <summary>
/// Raw TCP socket session (the legacy "RAW" protocol, PuTTY's "Raw" connection type).
/// Bytes are passed through unchanged — no Telnet option negotiation — and shown in the shared
/// <see cref="TerminalView"/>.
///
/// Like PuTTY's raw mode with its default "auto" settings, the remote end does not echo, so input is
/// line-edited locally: typed characters are echoed, Backspace edits the pending line, and Enter sends
/// the line terminated by CR LF.
/// </summary>
public sealed class RawSocketProtocol : ProtocolBase, IVisualProtocol
{
    private readonly ILogger<RawSocketProtocol> _logger;
    private readonly StringBuilder _pendingLine = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private TerminalView? _view;
    private CancellationTokenSource? _cts;

    public RawSocketProtocol(ILogger<RawSocketProtocol> logger) => _logger = logger;

    public Control CreateView()
    {
        _view = new TerminalView();
        _view.DataToSend += (_, data) => _ = HandleInputAsync(data);
        return _view;
    }

    public override async Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        State = ConnectionState.Connecting;
        RaiseStatus($"Connecting (raw) to {parameters.DisplayName}…");
        try
        {
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(parameters.Hostname, parameters.Port, ct);
            _stream = _tcp.GetStream();

            State = ConnectionState.Connected;
            RaiseStatus($"Connected to {parameters.DisplayName}");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _ = ReadLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            State = ConnectionState.Error;
            RaiseStatus($"Connection failed: {ex.Message}");
            _logger.LogError(ex, "Raw socket connect failed");
            throw;
        }
    }

    public override Task DisconnectAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        _stream?.Close();
        _tcp?.Close();
        State = ConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    /// <summary>Applies local line editing to keyboard input and sends completed lines.</summary>
    internal async Task HandleInputAsync(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        // Cursor and function keys arrive as escape sequences; a line editor ignores them.
        if (text.StartsWith('\x1b'))
            return;

        string? lineToSend = null;
        var echo = new StringBuilder();

        lock (_pendingLine)
        {
            foreach (var c in text)
            {
                switch (c)
                {
                    case '\r' or '\n':
                        lineToSend = (lineToSend ?? "") + _pendingLine + "\r\n";
                        _pendingLine.Clear();
                        echo.Append("\r\n");
                        break;
                    case '\x7f' or '\b':
                        if (_pendingLine.Length > 0)
                        {
                            _pendingLine.Length--;
                            echo.Append('\b');
                        }
                        break;
                    default:
                        if (!char.IsControl(c) || c == '\t')
                        {
                            _pendingLine.Append(c);
                            echo.Append(c);
                        }
                        break;
                }
            }
        }

        if (echo.Length > 0)
            _view?.Write(echo.ToString());
        if (lineToSend is not null)
            await SendAsync(Encoding.UTF8.GetBytes(lineToSend));
    }

    private async Task SendAsync(byte[] bytes)
    {
        if (_stream is null) return;
        await _writeLock.WaitAsync();
        try
        {
            await _stream.WriteAsync(bytes);
            await _stream.FlushAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Raw socket write failed");
            RaiseStatus($"Send failed: {ex.Message}");
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
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await _stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                int count = decoder.GetChars(buffer, 0, read, chars, 0);
                if (count > 0)
                    _view?.Write(new string(chars, 0, count));
            }

            if (!ct.IsCancellationRequested)
            {
                RaiseStatus("Connection closed by remote host.");
                State = ConnectionState.Disconnected;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Raw socket read loop ended");
            RaiseStatus($"Connection lost: {ex.Message}");
            State = ConnectionState.Error;
        }
        catch (Exception)
        {
            // Socket closed by DisconnectAsync.
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
            _writeLock.Dispose();
        }
    }
}
