using System.Buffers.Binary;
using System.Text;

namespace mRemoteNG.Protocols.Vnc.Rfb;

/// <summary>
/// Buffered, blocking big-endian reader for the server → client half of an RFB stream.
/// Not thread-safe: only the receive loop (or the handshake that precedes it) reads.
/// </summary>
public sealed class RfbReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer;
    private int _position;
    private int _length;

    public RfbReader(Stream stream, int bufferSize = 64 * 1024)
    {
        _stream = stream;
        _buffer = new byte[bufferSize];
    }

    /// <summary>Fills <paramref name="destination"/> completely or throws <see cref="EndOfStreamException"/>.</summary>
    public void ReadExactly(Span<byte> destination)
    {
        while (!destination.IsEmpty)
        {
            if (_position == _length)
            {
                // Large payloads bypass the buffer.
                if (destination.Length >= _buffer.Length)
                {
                    var direct = _stream.Read(destination);
                    if (direct <= 0) throw ConnectionClosed();
                    destination = destination[direct..];
                    continue;
                }
                Fill();
            }

            var count = Math.Min(destination.Length, _length - _position);
            _buffer.AsSpan(_position, count).CopyTo(destination);
            _position += count;
            destination = destination[count..];
        }
    }

    public byte[] ReadBytes(int count)
    {
        var bytes = new byte[count];
        ReadExactly(bytes);
        return bytes;
    }

    public void Skip(int count)
    {
        Span<byte> scratch = stackalloc byte[256];
        while (count > 0)
        {
            var chunk = Math.Min(count, scratch.Length);
            ReadExactly(scratch[..chunk]);
            count -= chunk;
        }
    }

    public byte ReadByte()
    {
        if (_position == _length) Fill();
        return _buffer[_position++];
    }

    public ushort ReadUInt16()
    {
        Span<byte> b = stackalloc byte[2];
        ReadExactly(b);
        return BinaryPrimitives.ReadUInt16BigEndian(b);
    }

    public uint ReadUInt32()
    {
        Span<byte> b = stackalloc byte[4];
        ReadExactly(b);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    public int ReadInt32()
    {
        Span<byte> b = stackalloc byte[4];
        ReadExactly(b);
        return BinaryPrimitives.ReadInt32BigEndian(b);
    }

    /// <summary>Reads a 32bpp little-endian pixel (the only format this client negotiates) as opaque BGRA.</summary>
    public uint ReadPixel()
    {
        Span<byte> b = stackalloc byte[4];
        ReadExactly(b);
        return BinaryPrimitives.ReadUInt32LittleEndian(b) | 0xFF000000u;
    }

    /// <summary>Reads one pixel in the negotiated format as opaque BGRA.</summary>
    public uint ReadPixel(PixelConverter converter)
    {
        Span<byte> b = stackalloc byte[4];
        var pixel = b[..converter.BytesPerPixel];
        ReadExactly(pixel);
        return converter.ReadPixel(pixel);
    }

    /// <summary>Reads a u32 length-prefixed Latin-1 string, as used for reasons, names and cut text.</summary>
    public string ReadString(int maxLength = 16 * 1024 * 1024)
    {
        var length = ReadUInt32();
        if (length > maxLength)
            throw new RfbProtocolException($"String of {length} bytes exceeds the {maxLength} byte limit.");
        return Encoding.Latin1.GetString(ReadBytes((int)length));
    }

    private void Fill()
    {
        var read = _stream.Read(_buffer, 0, _buffer.Length);
        if (read <= 0) throw ConnectionClosed();
        _position = 0;
        _length = read;
    }

    private static EndOfStreamException ConnectionClosed() =>
        new("The VNC server closed the connection.");
}
