using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary>Builds big-endian RFB byte streams for tests.</summary>
internal sealed class RfbBytes
{
    private readonly List<byte> _bytes = [];

    public RfbBytes U8(int value)
    {
        _bytes.Add((byte)value);
        return this;
    }

    public RfbBytes U16(int value)
    {
        _bytes.Add((byte)(value >> 8));
        _bytes.Add((byte)value);
        return this;
    }

    public RfbBytes U32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        _bytes.AddRange(b.ToArray());
        return this;
    }

    public RfbBytes S32(int value) => U32(unchecked((uint)value));

    /// <summary>A 32bpp little-endian pixel for colour 0xRRGGBB (wire bytes B, G, R, 0).</summary>
    public RfbBytes Pixel(uint rgb) => Raw((byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16), 0);

    /// <summary>A ZRLE compressed pixel for colour 0xRRGGBB (wire bytes B, G, R).</summary>
    public RfbBytes CPixel(uint rgb) => Raw((byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16));

    public RfbBytes Raw(params byte[] bytes)
    {
        _bytes.AddRange(bytes);
        return this;
    }

    public RfbBytes Ascii(string text) => Raw(Encoding.ASCII.GetBytes(text));

    public RfbBytes String(string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        return U32((uint)bytes.Length).Raw(bytes);
    }

    public RfbBytes Rect(int x, int y, int w, int h, int encoding) => U16(x).U16(y).U16(w).U16(h).S32(encoding);

    public byte[] ToArray() => _bytes.ToArray();

    public static implicit operator byte[](RfbBytes b) => b.ToArray();
}

/// <summary>Framebuffer value for colour 0xRRGGBB.</summary>
internal static class Colour
{
    public static uint Of(uint rgb) => 0xFF000000u | rgb;
}

/// <summary>A scripted single-connection RFB server on a loopback port.</summary>
internal sealed class FakeRfbServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private TcpClient? _client;

    public FakeRfbServer() => _listener.Start();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public NetworkStream Stream { get; private set; } = null!;

    public async Task AcceptAsync()
    {
        _client = await _listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
        _client.NoDelay = true;
        Stream = _client.GetStream();
    }

    public Task SendAsync(byte[] bytes) => Stream.WriteAsync(bytes).AsTask();

    public async Task<byte[]> ReceiveAsync(int count)
    {
        var buffer = new byte[count];
        await Stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }

    /// <summary>Reads bytes until <paramref name="terminator"/> (inclusive), e.g. the end of HTTP headers.</summary>
    public async Task<string> ReceiveUntilAsync(string terminator)
    {
        var text = new StringBuilder();
        while (!text.ToString().EndsWith(terminator, StringComparison.Ordinal))
            text.Append((char)(await ReceiveAsync(1))[0]);
        return text.ToString();
    }

    /// <summary>Sends the version banner and returns the version string the client chose.</summary>
    public async Task<string> NegotiateVersionAsync(string serverVersion)
    {
        await SendAsync(Encoding.ASCII.GetBytes($"RFB {serverVersion}\n"));
        return Encoding.ASCII.GetString(await ReceiveAsync(12));
    }

    /// <summary>Reads ClientInit, sends ServerInit, then reads the SetPixelFormat and SetEncodings messages.</summary>
    public async Task<(byte Shared, byte[] SetPixelFormat, int[] Encodings)> InitialiseAsync(int width, int height, string name)
    {
        var shared = (await ReceiveAsync(1))[0];
        await SendAsync(new RfbBytes().U16(width).U16(height)
            // Server's native format: 16bpp, to prove the client asks for its own format regardless.
            .U8(16).U8(16).U8(0).U8(1).U16(31).U16(63).U16(31).U8(11).U8(5).U8(0).U8(0).U8(0).U8(0)
            .String(name));

        var setPixelFormat = await ReceiveAsync(20);
        var header = await ReceiveAsync(4);
        var count = header[2] << 8 | header[3];
        var raw = await ReceiveAsync(4 * count);
        var encodings = Enumerable.Range(0, count)
            .Select(i => BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(4 * i)))
            .ToArray();
        return (shared, setPixelFormat, encodings);
    }

    public void CloseConnection() => _client?.Close();

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _listener.Stop();
        await Task.CompletedTask;
    }
}
