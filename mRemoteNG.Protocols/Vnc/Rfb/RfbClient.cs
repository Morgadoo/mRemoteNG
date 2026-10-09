using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using mRemoteNG.Protocols.Vnc.Rfb.Decoders;

namespace mRemoteNG.Protocols.Vnc.Rfb;

public sealed class RfbClientOptions
{
    /// <summary>Encodings offered to the server, most preferred first.</summary>
    public static IReadOnlyList<int> DefaultEncodings { get; } =
    [
        RfbEncoding.CopyRect,
        RfbEncoding.Zrle,
        RfbEncoding.Hextile,
        RfbEncoding.Rre,
        RfbEncoding.Raw,
        RfbEncoding.DesktopSize,
        RfbEncoding.ExtendedDesktopSize,
        RfbEncoding.LastRect,
        RfbEncoding.Cursor,
    ];

    /// <summary>Password for VNC Authentication; null when none is configured.</summary>
    public string? Password { get; init; }

    /// <summary>ClientInit shared flag: leave other viewers connected.</summary>
    public bool Shared { get; init; } = true;

    public IReadOnlyList<int> Encodings { get; init; } = DefaultEncodings;
}

/// <summary>Cursor shape sent through the Cursor pseudo-encoding; transparent pixels are 0.</summary>
public sealed record RfbCursor(int Width, int Height, int HotspotX, int HotspotY, uint[] Bgra);

/// <summary>
/// A managed RFB 3.3 / 3.7 / 3.8 client independent of any UI toolkit.
/// <para>
/// <see cref="ConnectAsync"/> performs the handshake, security negotiation and initialisation, then requests
/// 32bpp BGRA pixels. <see cref="Start"/> runs the receive loop on a dedicated background thread, which decodes
/// updates into <see cref="Framebuffer"/> and raises events on that thread. Input messages may be sent from any
/// thread; they are queued and written by a separate send loop so callers never block on the network.
/// </para>
/// </summary>
public sealed class RfbClient : IDisposable
{
    private readonly Stream _stream;
    private readonly RfbReader _reader;
    private readonly IReadOnlyList<int> _encodings;
    private readonly Channel<byte[]> _sendQueue =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<int, IRectangleDecoder> _decoders = new()
    {
        [RfbEncoding.Raw] = new RawDecoder(),
        [RfbEncoding.CopyRect] = new CopyRectDecoder(),
        [RfbEncoding.Rre] = new RreDecoder(),
        [RfbEncoding.Hextile] = new HextileDecoder(),
        [RfbEncoding.Zrle] = new ZrleDecoder(),
    };
    private readonly ConcurrentDictionary<int, long> _rectangleCounts = new();
    private Task _receiveTask = Task.CompletedTask;
    private Task _sendTask = Task.CompletedTask;
    private int _started;
    private volatile bool _disposed;

    private RfbClient(Stream stream, RfbReader reader, IReadOnlyList<int> encodings)
    {
        _stream = stream;
        _reader = reader;
        _encodings = encodings;
        Framebuffer = new Framebuffer(0, 0);
    }

    /// <summary>Negotiated protocol version, e.g. 3.8.</summary>
    public Version ProtocolVersion { get; private set; } = new(3, 3);
    public byte SecurityType { get; private set; }
    public string DesktopName { get; private set; } = string.Empty;
    public PixelFormat ServerPixelFormat { get; private set; }
    public Framebuffer Framebuffer { get; }

    /// <summary>Number of rectangles received per encoding (including pseudo-encodings), for diagnostics.</summary>
    public IReadOnlyDictionary<int, long> RectangleCounts => _rectangleCounts;

    /// <summary>Raised after every complete FramebufferUpdate; read changes via <see cref="Rfb.Framebuffer.TakeDirty"/>.</summary>
    public event EventHandler? FramebufferUpdated;

    /// <summary>Raised when the server changes the desktop size (before the matching FramebufferUpdated).</summary>
    public event EventHandler? DesktopResized;

    public event EventHandler? BellReceived;
    public event EventHandler<string>? ServerCutTextReceived;
    public event EventHandler<RfbCursor>? CursorChanged;

    /// <summary>
    /// Raised once when the receive loop ends: with the error that ended it, or null when the client was disposed.
    /// </summary>
    public event EventHandler<Exception?>? Disconnected;

    /// <summary>Completes when the receive loop has ended.</summary>
    public Task Completion => _receiveTask;

    /// <summary>
    /// Performs the RFB handshake on <paramref name="stream"/> up to and including ServerInit, then sends
    /// SetPixelFormat and SetEncodings. The stream is disposed if the handshake fails or is cancelled.
    /// </summary>
    /// <exception cref="RfbAuthenticationException">The server refused the connection or the credentials.</exception>
    /// <exception cref="RfbProtocolException">The peer is not a usable VNC server.</exception>
    public static async Task<RfbClient> ConnectAsync(Stream stream, RfbClientOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);

        var client = new RfbClient(stream, new RfbReader(stream), options.Encodings);
        // The handshake uses blocking reads; cancellation closes the stream to unblock them.
        await using var registration = ct.Register(stream.Dispose);
        try
        {
            await Task.Run(() => client.Handshake(options), CancellationToken.None).ConfigureAwait(false);
            return client;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new OperationCanceledException(ct);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Starts the receive and send loops and asks for the whole framebuffer.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The client has already been started.");

        _sendTask = Task.Run(SendLoopAsync);
        RequestUpdate(incremental: false);
        _receiveTask = Task.Factory.StartNew(ReceiveLoop, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // ── Client → server ────────────────────────────────────────────────────

    public void RequestUpdate(bool incremental)
    {
        var fb = Framebuffer.Snapshot;
        var msg = new byte[10];
        msg[0] = RfbClientMessage.FramebufferUpdateRequest;
        msg[1] = (byte)(incremental ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(6), (ushort)fb.Width);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(8), (ushort)fb.Height);
        Enqueue(msg);
    }

    /// <summary>Sends an X11 keysym press or release.</summary>
    public void SendKeyEvent(uint keysym, bool down)
    {
        var msg = new byte[8];
        msg[0] = RfbClientMessage.KeyEvent;
        msg[1] = (byte)(down ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), keysym);
        Enqueue(msg);
    }

    /// <summary>Sends the pointer position (framebuffer pixels) and the currently pressed buttons.</summary>
    public void SendPointerEvent(int x, int y, RfbButtons buttons)
    {
        var msg = new byte[6];
        msg[0] = RfbClientMessage.PointerEvent;
        msg[1] = (byte)buttons;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(2), (ushort)Math.Clamp(x, 0, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4), (ushort)Math.Clamp(y, 0, ushort.MaxValue));
        Enqueue(msg);
    }

    /// <summary>Sends clipboard text to the server. RFB cut text is Latin-1; other characters become '?'.</summary>
    public void SendClientCutText(string text)
    {
        var latin1 = Encoding.GetEncoding("ISO-8859-1", EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        var bytes = latin1.GetBytes(text.Replace("\r\n", "\n"));
        var msg = new byte[8 + bytes.Length];
        msg[0] = RfbClientMessage.ClientCutText;
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), (uint)bytes.Length);
        bytes.CopyTo(msg, 8);
        Enqueue(msg);
    }

    private void Enqueue(byte[] message)
    {
        if (!_disposed) _sendQueue.Writer.TryWrite(message);
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var message in _sendQueue.Reader.ReadAllAsync().ConfigureAwait(false))
                await _stream.WriteAsync(message).ConfigureAwait(false);
        }
        catch (Exception) when (_disposed)
        {
            // Closing the stream interrupts a pending write.
        }
        catch (Exception)
        {
            // A broken connection also ends the receive loop, which reports it; just unblock the stream.
            _stream.Dispose();
        }
    }

    // ── Handshake ──────────────────────────────────────────────────────────

    private void Handshake(RfbClientOptions options)
    {
        NegotiateVersion();
        NegotiateSecurity(options.Password);

        _stream.Write([(byte)(options.Shared ? 1 : 0)]);

        int width = _reader.ReadUInt16();
        int height = _reader.ReadUInt16();
        ServerPixelFormat = PixelFormat.Read(_reader);
        DesktopName = _reader.ReadString(maxLength: 64 * 1024);
        Framebuffer.Resize(width, height);

        var setPixelFormat = new byte[4 + PixelFormat.Size];
        setPixelFormat[0] = RfbClientMessage.SetPixelFormat;
        PixelFormat.Bgra32.Write(setPixelFormat.AsSpan(4));
        _stream.Write(setPixelFormat);

        var setEncodings = new byte[4 + 4 * _encodings.Count];
        setEncodings[0] = RfbClientMessage.SetEncodings;
        BinaryPrimitives.WriteUInt16BigEndian(setEncodings.AsSpan(2), (ushort)_encodings.Count);
        for (var i = 0; i < _encodings.Count; i++)
            BinaryPrimitives.WriteInt32BigEndian(setEncodings.AsSpan(4 + 4 * i), _encodings[i]);
        _stream.Write(setEncodings);
        _stream.Flush();
    }

    private void NegotiateVersion()
    {
        var banner = Encoding.ASCII.GetString(_reader.ReadBytes(12));
        if (!banner.StartsWith("RFB ", StringComparison.Ordinal) || banner[7] != '.' || banner[11] != '\n'
            || !int.TryParse(banner.AsSpan(4, 3), out var major) || !int.TryParse(banner.AsSpan(8, 3), out var minor))
        {
            throw new RfbProtocolException($"The server did not send an RFB version banner (got \"{Printable(banner)}\"). Is this a VNC port?");
        }
        if (major < 3)
            throw new RfbProtocolException($"Unsupported RFB version {major}.{minor}.");

        // 3.3 is the fallback for unofficial minors (3.4, 3.6 …); anything newer than 3.8 speaks 3.8.
        var chosen = major > 3 || minor >= 8 ? 8 : minor == 7 ? 7 : 3;
        ProtocolVersion = new Version(3, chosen);
        _stream.Write(Encoding.ASCII.GetBytes($"RFB 003.00{chosen}\n"));
    }

    private void NegotiateSecurity(string? password)
    {
        var minor = ProtocolVersion.Minor;
        byte type;
        if (minor == 3)
        {
            var offered = _reader.ReadUInt32();
            if (offered == RfbSecurityType.Invalid)
                throw new RfbAuthenticationException($"The server refused the connection: {_reader.ReadString()}");
            if (offered is not (RfbSecurityType.None or RfbSecurityType.VncAuthentication))
                throw new RfbProtocolException($"The server requires unsupported security type {SecurityTypeName((byte)offered)}.");
            type = (byte)offered;
        }
        else
        {
            int count = _reader.ReadByte();
            if (count == 0)
                throw new RfbAuthenticationException($"The server refused the connection: {_reader.ReadString()}");
            var offered = _reader.ReadBytes(count);
            if (offered.Contains(RfbSecurityType.None))
                type = RfbSecurityType.None;
            else if (offered.Contains(RfbSecurityType.VncAuthentication))
                type = RfbSecurityType.VncAuthentication;
            else
                throw new RfbProtocolException("The server offers no supported security type (offered: "
                    + string.Join(", ", offered.Select(SecurityTypeName)) + "). Supported: None, VNC Authentication.");
            _stream.Write([type]);
        }
        SecurityType = type;

        if (type == RfbSecurityType.VncAuthentication)
        {
            var challenge = _reader.ReadBytes(VncAuthentication.ChallengeLength);
            if (password is null)
                throw new RfbAuthenticationException("The VNC server requires a password, but none is configured for this connection.");
            _stream.Write(VncAuthentication.ComputeResponse(password, challenge));
        }

        // RFB 3.3 and 3.7 send no SecurityResult for security type None.
        if (type == RfbSecurityType.None && minor < 8)
            return;

        var result = _reader.ReadUInt32();
        if (result == 0)
            return;

        string? reason = null;
        if (minor >= 8)
        {
            try { reason = _reader.ReadString(maxLength: 64 * 1024); }
            catch (IOException) { /* some servers just close the connection */ }
        }
        reason ??= result == 2 ? "too many authentication attempts" : "the server rejected the credentials";
        throw new RfbAuthenticationException(type == RfbSecurityType.VncAuthentication
            ? $"VNC authentication failed: {reason}"
            : $"The server rejected the connection: {reason}");
    }

    private static string SecurityTypeName(byte type) => type switch
    {
        RfbSecurityType.None => "None",
        RfbSecurityType.VncAuthentication => "VNC Authentication",
        5 or 6 => $"RA2 ({type})",
        16 => "Tight (16)",
        18 => "TLS (18)",
        19 => "VeNCrypt (19)",
        30 => "Apple Remote Desktop (30)",
        113 => "MSLogon II (113)",
        _ => type.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string Printable(string s) =>
        new(s.Select(c => c is >= ' ' and < (char)0x7F ? c : '.').ToArray());

    // ── Server → client ────────────────────────────────────────────────────

    private void ReceiveLoop()
    {
        Exception? error = null;
        try
        {
            while (true)
            {
                var type = _reader.ReadByte();
                switch (type)
                {
                    case RfbServerMessage.FramebufferUpdate:
                        ReadFramebufferUpdate();
                        break;
                    case RfbServerMessage.SetColourMapEntries:
                        // Only true-colour is negotiated; a palette is irrelevant, but must be consumed.
                        _reader.Skip(1);
                        _reader.ReadUInt16();
                        _reader.Skip(_reader.ReadUInt16() * 6);
                        break;
                    case RfbServerMessage.Bell:
                        BellReceived?.Invoke(this, EventArgs.Empty);
                        break;
                    case RfbServerMessage.ServerCutText:
                        _reader.Skip(3);
                        ServerCutTextReceived?.Invoke(this, _reader.ReadString());
                        break;
                    default:
                        throw new RfbProtocolException($"Unknown server message type {type}.");
                }
            }
        }
        catch (Exception ex)
        {
            if (!_disposed) error = ex;
        }
        finally
        {
            _sendQueue.Writer.TryComplete();
            if (!_disposed) _stream.Dispose();
            Disconnected?.Invoke(this, error);
        }
    }

    private void ReadFramebufferUpdate()
    {
        _reader.Skip(1);
        int count = _reader.ReadUInt16();
        var resized = false;

        for (var i = 0; i < count; i++)
        {
            var rect = new RfbRect(_reader.ReadUInt16(), _reader.ReadUInt16(), _reader.ReadUInt16(), _reader.ReadUInt16());
            var encoding = _reader.ReadInt32();
            _rectangleCounts.AddOrUpdate(encoding, 1, (_, n) => n + 1);

            if (_decoders.TryGetValue(encoding, out var decoder))
            {
                decoder.Decode(_reader, Framebuffer, rect);
                Framebuffer.MarkDirty(rect);
                continue;
            }

            switch (encoding)
            {
                case RfbEncoding.DesktopSize:
                    resized |= Framebuffer.Resize(rect.Width, rect.Height);
                    break;

                case RfbEncoding.ExtendedDesktopSize:
                    // x = reason, y = status (0 = success); followed by the screen layout, which is not needed.
                    // Servers such as TigerVNC repeat the current layout after every non-incremental request,
                    // so only an actual size change counts (otherwise re-requesting the full screen would loop).
                    int screens = _reader.ReadByte();
                    _reader.Skip(3 + 16 * screens);
                    if (rect.Y == 0)
                        resized |= Framebuffer.Resize(rect.Width, rect.Height);
                    break;

                case RfbEncoding.Cursor:
                    CursorChanged?.Invoke(this, ReadCursor(rect));
                    break;

                case RfbEncoding.LastRect:
                    i = count;
                    break;

                default:
                    throw new RfbProtocolException($"The server sent rectangle encoding {encoding}, which was not requested.");
            }
        }

        if (resized) DesktopResized?.Invoke(this, EventArgs.Empty);
        FramebufferUpdated?.Invoke(this, EventArgs.Empty);
        // After a resize, ask for the whole (new) desktop rather than changes.
        RequestUpdate(incremental: !resized);
    }

    private RfbCursor ReadCursor(RfbRect rect)
    {
        var pixels = new uint[rect.Width * rect.Height];
        var wire = new byte[pixels.Length * 4];
        _reader.ReadExactly(wire);
        var maskStride = (rect.Width + 7) / 8;
        var mask = _reader.ReadBytes(maskStride * rect.Height);

        for (var y = 0; y < rect.Height; y++)
        {
            for (var x = 0; x < rect.Width; x++)
            {
                var visible = (mask[y * maskStride + x / 8] & (0x80 >> (x % 8))) != 0;
                var i = y * rect.Width + x;
                pixels[i] = visible ? BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(i * 4)) | 0xFF000000u : 0;
            }
        }
        // For the Cursor pseudo-encoding the rectangle position is the hotspot.
        return new RfbCursor(rect.Width, rect.Height, rect.X, rect.Y, pixels);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sendQueue.Writer.TryComplete();
        _stream.Dispose();
    }
}
