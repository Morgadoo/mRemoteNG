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
///
/// Window-size changes (BSD rlogin): one urgent byte <c>0x80</c> (<c>TIOCPKT_WINDOW</c>), then
/// twelve in-band bytes — <c>FF FF 's' 's'</c> and rows, columns, x pixels, y pixels as
/// big-endian 16-bit values. Sent after the handshake and on every terminal resize.
/// </summary>
public sealed class RloginProtocol : ProtocolBase, IVisualProtocol, ITerminalProtocol
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);

    private readonly ILogger<RloginProtocol> _logger;
    private readonly Decoder _utf8 = new UTF8Encoding(false).GetDecoder();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private TerminalView? _view;
    private CancellationTokenSource? _cts;

    /// <summary>Decoded server text; lets tests observe output without a view.</summary>
    internal event Action<string>? TextReceived;

    public RloginProtocol(ILogger<RloginProtocol> logger) => _logger = logger;

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
        RaiseStatus($"Connecting (rlogin) to {parameters.DisplayName}…");
        try
        {
            _tcp = new TcpClient();
            await _tcp.ConnectAsync(parameters.Hostname, parameters.Port, ct);
            _stream = _tcp.GetStream();

            await _stream.WriteAsync(BuildHandshake(Environment.UserName, parameters.Username), ct);

            // The server acknowledges with a single zero byte.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(HandshakeTimeout);
            var ack = new byte[1];
            int read = await _stream.ReadAsync(ack, timeout.Token);
            if (read == 0)
                throw new InvalidOperationException("The server closed the connection during the rlogin handshake.");
            if (ack[0] != 0)
                throw new InvalidOperationException($"Rlogin handshake failed (expected 0x00, got 0x{ack[0]:X2}).");

            State = ConnectionState.Connected;
            RaiseStatus($"Connected (rlogin) to {parameters.DisplayName}");

            if (_view is not null)
                await SendWindowSizeAsync(_view.TerminalCols, _view.TerminalRows, ct: ct);

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

    /// <summary>RFC 1282: NUL, client user NUL, server user NUL, terminal-type/speed NUL.</summary>
    internal static byte[] BuildHandshake(string clientUser, string? serverUser)
    {
        var handshake = new List<byte> { 0x00 };
        handshake.AddRange(Encoding.ASCII.GetBytes(clientUser)); handshake.Add(0);
        handshake.AddRange(Encoding.ASCII.GetBytes(serverUser ?? clientUser)); handshake.Add(0);
        handshake.AddRange(Encoding.ASCII.GetBytes("xterm-256color/38400")); handshake.Add(0);
        return handshake.ToArray();
    }

    /// <summary>Urgent byte that tells the server a window-size message follows (<c>TIOCPKT_WINDOW</c>).</summary>
    internal const byte WindowSizeMagic = 0x80;

    /// <summary>
    /// The twelve-byte in-band window message: <c>FF FF 's' 's'</c>, then rows, columns,
    /// horizontal pixels and vertical pixels, each a big-endian unsigned 16-bit value.
    /// </summary>
    internal static byte[] BuildWindowChange(int columns, int rows, int pixelWidth = 0, int pixelHeight = 0)
    {
        var bytes = new byte[12];
        bytes[0] = 0xFF;
        bytes[1] = 0xFF;
        bytes[2] = (byte)'s';
        bytes[3] = (byte)'s';
        WriteUInt16(bytes, 4, rows);
        WriteUInt16(bytes, 6, columns);
        WriteUInt16(bytes, 8, pixelWidth);
        WriteUInt16(bytes, 10, pixelHeight);
        return bytes;
    }

    /// <summary>
    /// Tells the server the terminal size. No-op when not connected. A failure to send the urgent
    /// byte skips the in-band message too: without the marker the server would treat it as keystrokes.
    /// </summary>
    internal async Task SendWindowSizeAsync(int columns, int rows, int pixelWidth = 0, int pixelHeight = 0, CancellationToken ct = default)
    {
        if (_tcp?.Client is not Socket socket || !socket.Connected || _stream is null)
            return;

        var payload = BuildWindowChange(columns, rows, pixelWidth, pixelHeight);
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_tcp?.Client is not Socket current || !current.Connected || _stream is null)
                return;
            try
            {
                await current.SendAsync(new ReadOnlyMemory<byte>([WindowSizeMagic]), SocketFlags.OutOfBand, ct);
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "Rlogin window-size urgent data was not sent");
                return;
            }

            await _stream.WriteAsync(payload, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void WriteUInt16(byte[] buffer, int offset, int value)
    {
        var clamped = (ushort)Math.Clamp(value, 0, ushort.MaxValue);
        buffer[offset] = (byte)(clamped >> 8);
        buffer[offset + 1] = (byte)clamped;
    }

    private void OnTerminalResized(object? sender, TerminalSizeEventArgs e) =>
        _ = SendSafelyAsync(() => SendWindowSizeAsync(e.Columns, e.Rows, e.PixelWidth, e.PixelHeight));

    private async Task SendSafelyAsync(Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _logger.LogDebug(ex, "Rlogin write failed");
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

    /// <summary>Sends terminal input to the server (rlogin passes bytes through unchanged); ignored while not connected.</summary>
    public async Task SendInputAsync(byte[] input, CancellationToken ct = default)
    {
        if (_stream is null || input.Length == 0) return;
        await _writeLock.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(input, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async void OnTerminalDataToSend(object? sender, byte[] data)
    {
        try
        {
            await SendInputAsync(data);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            _logger.LogDebug(ex, "Rlogin write failed");
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_stream is null) return;
        var buffer = new byte[4096];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await _stream.ReadAsync(buffer, ct);
                if (read == 0) break;

                // Stateful decode: a UTF-8 character split across reads is kept for the next one.
                int count = _utf8.GetChars(buffer, 0, read, chars, 0, flush: false);
                if (count == 0) continue;
                var text = new string(chars, 0, count);
                _view?.Write(text);
                TextReceived?.Invoke(text);
            }

            State = ConnectionState.Disconnected;
            RaiseStatus("Connection closed by the remote host.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Rlogin read loop ended");
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
