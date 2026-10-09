namespace mRemoteNG.Protocols.Vnc.Rfb.Decoders;

/// <summary>
/// Encoding 16 (ZRLE): zlib-compressed 64×64 tiles using raw, solid, packed-palette, plain RLE and palette RLE
/// sub-encodings. One zlib stream spans the whole connection, so a single inflater is kept for the decoder's
/// lifetime and each rectangle's compressed bytes are appended to its input.
/// </summary>
internal sealed class ZrleDecoder(PixelConverter? converter = null) : IRectangleDecoder
{
    private const int TileSize = 64;

    private readonly PixelConverter _converter = converter ?? PixelConverter.Bgra32;
    private readonly ZlibInflater _inflater = new();
    private readonly uint[] _tile = new uint[TileSize * TileSize];
    private readonly uint[] _palette = new uint[128];
    private byte[] _data = new byte[64 * 1024];
    private int _dataLength;
    private int _pos;

    public void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect)
    {
        var length = reader.ReadUInt32();
        _inflater.Append(reader, (int)Math.Min(length, int.MaxValue));
        _dataLength = _inflater.ReadAll(ref _data);
        _pos = 0;

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
    /// A compressed pixel (CPIXEL): for 32bpp depth-24 formats the padding byte is omitted
    /// (B, G, R for the default little-endian format); otherwise a plain pixel.
    /// </summary>
    private uint NextCPixel()
    {
        var size = _converter.CompactBytesPerPixel;
        if (_pos + size > _dataLength)
            throw new RfbProtocolException("ZRLE data ended before the rectangle was complete.");
        var p = _converter.ReadCompactPixel(_data.AsSpan(_pos, size));
        _pos += size;
        return p;
    }
}
