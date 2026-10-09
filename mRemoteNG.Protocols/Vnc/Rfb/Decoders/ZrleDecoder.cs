using System.IO.Compression;

namespace mRemoteNG.Protocols.Vnc.Rfb.Decoders;

/// <summary>
/// Encoding 16 (ZRLE): zlib-compressed 64×64 tiles using raw, solid, packed-palette, plain RLE and palette RLE
/// sub-encodings. One zlib stream spans the whole connection, so a single inflater is kept for the decoder's
/// lifetime and each rectangle's compressed bytes are appended to its input.
/// </summary>
internal sealed class ZrleDecoder : IRectangleDecoder
{
    private const int TileSize = 64;
    private const int MaxCompressedLength = 64 * 1024 * 1024;

    private readonly ContinuousInputStream _input = new();
    private readonly ZLibStream _inflater;
    private readonly uint[] _tile = new uint[TileSize * TileSize];
    private readonly uint[] _palette = new uint[128];
    private byte[] _data = new byte[64 * 1024];
    private int _dataLength;
    private int _pos;

    public ZrleDecoder() => _inflater = new ZLibStream(_input, CompressionMode.Decompress);

    public void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect)
    {
        var length = reader.ReadUInt32();
        if (length > MaxCompressedLength)
            throw new RfbProtocolException($"ZRLE rectangle of {length} bytes is implausibly large.");
        _input.Append(reader, (int)length);
        Inflate();

        for (var ty = rect.Y; ty < rect.Bottom; ty += TileSize)
        {
            var th = Math.Min(TileSize, rect.Bottom - ty);
            for (var tx = rect.X; tx < rect.Right; tx += TileSize)
            {
                var tw = Math.Min(TileSize, rect.Right - tx);
                DecodeTile(_tile.AsSpan(0, tw * th), tw, th);
                framebuffer.Write(tx, ty, tw, th, _tile.AsSpan(0, tw * th));
            }
        }
    }

    /// <summary>Drains everything the inflater can produce from the input received so far.</summary>
    private void Inflate()
    {
        _dataLength = 0;
        _pos = 0;
        while (true)
        {
            if (_dataLength == _data.Length)
                Array.Resize(ref _data, _data.Length * 2);
            var read = _inflater.Read(_data, _dataLength, _data.Length - _dataLength);
            if (read <= 0) break;
            _dataLength += read;
        }
    }

    private void DecodeTile(Span<uint> tile, int tw, int th)
    {
        int subencoding = NextByte();
        switch (subencoding)
        {
            case 0:
                for (var i = 0; i < tile.Length; i++) tile[i] = NextCPixel();
                break;

            case 1:
                tile.Fill(NextCPixel());
                break;

            case >= 2 and <= 16:
                ReadPalette(subencoding);
                DecodePackedPalette(tile, tw, th, subencoding);
                break;

            case 128:
                for (var i = 0; i < tile.Length;)
                {
                    var colour = NextCPixel();
                    var run = NextRunLength();
                    FillRun(tile, ref i, run, colour);
                }
                break;

            case >= 130:
                ReadPalette(subencoding - 128);
                for (var i = 0; i < tile.Length;)
                {
                    int index = NextByte();
                    var run = 1;
                    if ((index & 0x80) != 0)
                    {
                        index &= 0x7F;
                        run = NextRunLength();
                    }
                    if (index >= subencoding - 128)
                        throw new RfbProtocolException($"ZRLE palette index {index} out of range.");
                    FillRun(tile, ref i, run, _palette[index]);
                }
                break;

            default:
                throw new RfbProtocolException($"Invalid ZRLE tile sub-encoding {subencoding}.");
        }
    }

    private void DecodePackedPalette(Span<uint> tile, int tw, int th, int paletteSize)
    {
        var bits = paletteSize == 2 ? 1 : paletteSize <= 4 ? 2 : 4;
        var mask = (1 << bits) - 1;
        for (var y = 0; y < th; y++)
        {
            // Each row starts on a byte boundary; indices are packed most significant bits first.
            int current = 0, remaining = 0;
            for (var x = 0; x < tw; x++)
            {
                if (remaining == 0)
                {
                    current = NextByte();
                    remaining = 8;
                }
                remaining -= bits;
                var index = (current >> remaining) & mask;
                if (index >= paletteSize)
                    throw new RfbProtocolException($"ZRLE palette index {index} out of range.");
                tile[y * tw + x] = _palette[index];
            }
        }
    }

    private void ReadPalette(int size)
    {
        for (var i = 0; i < size; i++) _palette[i] = NextCPixel();
    }

    private int NextRunLength()
    {
        var run = 1;
        int b;
        do
        {
            b = NextByte();
            run += b;
        } while (b == 255);
        return run;
    }

    private static void FillRun(Span<uint> tile, ref int i, int run, uint colour)
    {
        if (run > tile.Length - i)
            throw new RfbProtocolException("ZRLE run extends past the end of the tile.");
        tile.Slice(i, run).Fill(colour);
        i += run;
    }

    private byte NextByte()
    {
        if (_pos >= _dataLength)
            throw new RfbProtocolException("ZRLE data ended before the rectangle was complete.");
        return _data[_pos++];
    }

    /// <summary>
    /// A compressed pixel: for the negotiated 32bpp depth-24 little-endian format the three least
    /// significant bytes (B, G, R) are sent and the padding byte omitted.
    /// </summary>
    private uint NextCPixel()
    {
        if (_pos + 3 > _dataLength)
            throw new RfbProtocolException("ZRLE data ended before the rectangle was complete.");
        var p = _data[_pos] | (uint)_data[_pos + 1] << 8 | (uint)_data[_pos + 2] << 16 | 0xFF000000u;
        _pos += 3;
        return p;
    }

    /// <summary>
    /// Read-only stream fed one compressed rectangle at a time. Reading past the fed data returns 0 rather than
    /// blocking, which tells the inflater to stop until the next rectangle arrives.
    /// </summary>
    private sealed class ContinuousInputStream : Stream
    {
        private byte[] _buffer = new byte[64 * 1024];
        private int _start;
        private int _end;

        public void Append(RfbReader reader, int count)
        {
            if (_start == _end) _start = _end = 0;
            if (_buffer.Length - _end < count)
            {
                var pending = _end - _start;
                var target = _buffer.Length >= pending + count ? _buffer : new byte[Math.Max(_buffer.Length * 2, pending + count)];
                Buffer.BlockCopy(_buffer, _start, target, 0, pending);
                _buffer = target;
                _start = 0;
                _end = pending;
            }
            reader.ReadExactly(_buffer.AsSpan(_end, count));
            _end += count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = Math.Min(buffer.Length, _end - _start);
            _buffer.AsSpan(_start, n).CopyTo(buffer);
            _start += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
