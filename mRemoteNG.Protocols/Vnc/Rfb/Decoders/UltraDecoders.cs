namespace mRemoteNG.Protocols.Vnc.Rfb.Decoders;

/// <summary>
/// Encoding 9 (UltraVNC "Ultra"): Raw pixels compressed with LZO1X; each rectangle is a u32 length followed by
/// that many compressed bytes. A length of 0 means the rectangle is unchanged.
/// </summary>
internal sealed class UltraDecoder(PixelConverter? converter = null) : IRectangleDecoder
{
    private const int MaxCompressedLength = 64 * 1024 * 1024;

    private readonly PixelConverter _converter = converter ?? PixelConverter.Bgra32;
    private byte[] _compressed = [];
    private byte[] _raw = [];
    private uint[] _pixels = [];

    public void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect)
    {
        var length = reader.ReadUInt32();
        if (length == 0) return;
        if (length > MaxCompressedLength)
            throw new RfbProtocolException($"Ultra rectangle of {length} compressed bytes is implausibly large.");
        if (_compressed.Length < length) _compressed = new byte[length];
        var compressed = _compressed.AsSpan(0, (int)length);
        reader.ReadExactly(compressed);

        var rowBytes = rect.Width * _converter.BytesPerPixel;
        var expected = rowBytes * rect.Height;
        if (expected == 0) return;
        if (_raw.Length < expected) _raw = new byte[expected];
        var raw = _raw.AsSpan(0, expected);
        var written = Lzo1x.Decompress(compressed, raw);
        if (written != expected)
            throw new RfbProtocolException($"Ultra rectangle decompressed to {written} bytes instead of {expected}.");

        if (_pixels.Length < rect.Width) _pixels = new uint[rect.Width];
        var pixels = _pixels.AsSpan(0, rect.Width);
        for (var y = 0; y < rect.Height; y++)
        {
            _converter.ConvertRow(raw.Slice(y * rowBytes, rowBytes), pixels);
            framebuffer.Write(rect.X, rect.Y + y, rect.Width, 1, pixels);
        }
    }
}

/// <summary>
/// Encoding 8 (UltraVNC ZlibHex): Hextile whose tiles may instead be zlib-compressed, either as raw pixels
/// (<c>ZlibRaw</c>) or as a compressed Hextile tile body (<c>ZlibHex</c>). Each kind uses its own zlib stream for the
/// whole connection, and each compressed tile is a u16 length followed by the data.
/// </summary>
internal sealed class ZlibHexDecoder(PixelConverter? converter = null) : IRectangleDecoder, IDisposable
{
    private const byte Raw = 1;
    private const byte BackgroundSpecified = 2;
    private const byte ForegroundSpecified = 4;
    private const byte AnySubrects = 8;
    private const byte SubrectsColoured = 16;
    private const byte ZlibRaw = 32;
    private const byte ZlibHex = 64;

    private readonly PixelConverter _converter = converter ?? PixelConverter.Bgra32;
    private readonly ZlibInflater _rawStream = new();
    private readonly ZlibInflater _hexStream = new();
    private readonly byte[] _wire = new byte[16 * 16 * 4];
    private readonly uint[] _tile = new uint[16 * 16];

    // As in the UltraVNC viewer, the colours carry over between tiles and rectangles, and a coloured
    // sub-rectangle also becomes the foreground.
    private uint _background = 0xFF000000u;
    private uint _foreground = 0xFF000000u;

    public void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect)
    {
        var bpp = _converter.BytesPerPixel;
        for (var ty = rect.Y; ty < rect.Bottom; ty += 16)
        {
            var th = Math.Min(16, rect.Bottom - ty);
            for (var tx = rect.X; tx < rect.Right; tx += 16)
            {
                var tw = Math.Min(16, rect.Right - tx);
                var tile = _tile.AsSpan(0, tw * th);
                var subencoding = reader.ReadByte();

                if ((subencoding & Raw) != 0)
                {
                    var wire = _wire.AsSpan(0, tw * th * bpp);
                    reader.ReadExactly(wire);
                    _converter.ConvertRow(wire, tile);
                }
                else if ((subencoding & ZlibRaw) != 0)
                {
                    _rawStream.Append(reader, reader.ReadUInt16());
                    var wire = _wire.AsSpan(0, tw * th * bpp);
                    _rawStream.ReadExactly(wire);
                    _converter.ConvertRow(wire, tile);
                }
                else if ((subencoding & ZlibHex) != 0)
                {
                    _hexStream.Append(reader, reader.ReadUInt16());
                    DecodeHextileBody(new InflaterSource(_hexStream), subencoding, tw, th, tile);
                }
                else
                {
                    DecodeHextileBody(new ReaderSource(reader), subencoding, tw, th, tile);
                }

                framebuffer.Write(tx, ty, tw, th, tile);
            }
        }
    }

    private void DecodeHextileBody<TSource>(TSource source, byte subencoding, int tw, int th, Span<uint> tile)
        where TSource : struct, IByteSource
    {
        var bpp = _converter.BytesPerPixel;
        Span<byte> pixel = stackalloc byte[4];
        pixel = pixel[..bpp];

        if ((subencoding & BackgroundSpecified) != 0)
        {
            source.Read(pixel);
            _background = _converter.ReadPixel(pixel);
        }
        if ((subencoding & ForegroundSpecified) != 0)
        {
            source.Read(pixel);
            _foreground = _converter.ReadPixel(pixel);
        }
        tile.Fill(_background);
        if ((subencoding & AnySubrects) == 0) return;

        Span<byte> one = stackalloc byte[2];
        source.Read(one[..1]);
        int count = one[0];
        var coloured = (subencoding & SubrectsColoured) != 0;
        for (var i = 0; i < count; i++)
        {
            if (coloured)
            {
                source.Read(pixel);
                _foreground = _converter.ReadPixel(pixel);
            }
            source.Read(one);
            int sx = one[0] >> 4, sy = one[0] & 0x0F;
            int sw = (one[1] >> 4) + 1, sh = (one[1] & 0x0F) + 1;
            var right = Math.Min(sx + sw, tw);
            var bottom = Math.Min(sy + sh, th);
            if (right <= sx) continue;
            for (var row = sy; row < bottom; row++)
                tile.Slice(row * tw + sx, right - sx).Fill(_foreground);
        }
    }

    public void Dispose()
    {
        _rawStream.Dispose();
        _hexStream.Dispose();
    }

    private interface IByteSource
    {
        void Read(Span<byte> destination);
    }

    private readonly struct ReaderSource(RfbReader reader) : IByteSource
    {
        public void Read(Span<byte> destination) => reader.ReadExactly(destination);
    }

    private readonly struct InflaterSource(ZlibInflater inflater) : IByteSource
    {
        public void Read(Span<byte> destination) => inflater.ReadExactly(destination);
    }
}
