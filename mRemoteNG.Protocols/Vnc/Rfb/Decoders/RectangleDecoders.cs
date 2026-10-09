namespace mRemoteNG.Protocols.Vnc.Rfb.Decoders;

/// <summary>Decodes one FramebufferUpdate rectangle of a given encoding into the framebuffer.</summary>
internal interface IRectangleDecoder
{
    void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect);
}

/// <summary>Encoding 0: width × height uncompressed pixels.</summary>
internal sealed class RawDecoder(PixelConverter? converter = null) : IRectangleDecoder
{
    private readonly PixelConverter _converter = converter ?? PixelConverter.Bgra32;
    private byte[] _row = [];
    private uint[] _pixels = [];

    public void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect)
    {
        var rowBytes = rect.Width * _converter.BytesPerPixel;
        if (_row.Length < rowBytes) _row = new byte[rowBytes];
        if (_pixels.Length < rect.Width) _pixels = new uint[rect.Width];
        var row = _row.AsSpan(0, rowBytes);
        var pixels = _pixels.AsSpan(0, rect.Width);

        for (var y = 0; y < rect.Height; y++)
        {
            reader.ReadExactly(row);
            _converter.ConvertRow(row, pixels);
            framebuffer.Write(rect.X, rect.Y + y, rect.Width, 1, pixels);
        }
    }
}

/// <summary>Encoding 1: copy a rectangle that is already on screen.</summary>
internal sealed class CopyRectDecoder : IRectangleDecoder
{
    public void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect)
    {
        int srcX = reader.ReadUInt16();
        int srcY = reader.ReadUInt16();
        framebuffer.Copy(srcX, srcY, rect.X, rect.Y, rect.Width, rect.Height);
    }
}

/// <summary>Encoding 2: background colour followed by solid sub-rectangles.</summary>
internal sealed class RreDecoder(PixelConverter? converter = null) : IRectangleDecoder
{
    private readonly PixelConverter _converter = converter ?? PixelConverter.Bgra32;

    public void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect)
    {
        var count = reader.ReadUInt32();
        framebuffer.Fill(rect.X, rect.Y, rect.Width, rect.Height, reader.ReadPixel(_converter));

        for (uint i = 0; i < count; i++)
        {
            var colour = reader.ReadPixel(_converter);
            int x = reader.ReadUInt16();
            int y = reader.ReadUInt16();
            int w = reader.ReadUInt16();
            int h = reader.ReadUInt16();
            // Sub-rectangles must stay inside their rectangle; clip defensively.
            var right = Math.Min(x + w, rect.Width);
            var bottom = Math.Min(y + h, rect.Height);
            if (right > x && bottom > y)
                framebuffer.Fill(rect.X + x, rect.Y + y, right - x, bottom - y, colour);
        }
    }
}

/// <summary>Encoding 4 (CoRRE): RRE with one-byte sub-rectangle coordinates (rectangles of at most 255×255).</summary>
internal sealed class CoRreDecoder(PixelConverter? converter = null) : IRectangleDecoder
{
    private readonly PixelConverter _converter = converter ?? PixelConverter.Bgra32;

    public void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect)
    {
        var count = reader.ReadUInt32();
        framebuffer.Fill(rect.X, rect.Y, rect.Width, rect.Height, reader.ReadPixel(_converter));

        for (uint i = 0; i < count; i++)
        {
            var colour = reader.ReadPixel(_converter);
            int x = reader.ReadByte();
            int y = reader.ReadByte();
            int w = reader.ReadByte();
            int h = reader.ReadByte();
            var right = Math.Min(x + w, rect.Width);
            var bottom = Math.Min(y + h, rect.Height);
            if (right > x && bottom > y)
                framebuffer.Fill(rect.X + x, rect.Y + y, right - x, bottom - y, colour);
        }
    }
}

/// <summary>Encoding 5: 16×16 tiles, each raw or background + (coloured) sub-rectangles.</summary>
internal sealed class HextileDecoder(PixelConverter? converter = null) : IRectangleDecoder
{
    private readonly PixelConverter _converter = converter ?? PixelConverter.Bgra32;
    private const byte Raw = 1;
    private const byte BackgroundSpecified = 2;
    private const byte ForegroundSpecified = 4;
    private const byte AnySubrects = 8;
    private const byte SubrectsColoured = 16;

    private readonly byte[] _rawTile = new byte[16 * 16 * 4];
    private readonly uint[] _rawPixels = new uint[16 * 16];
    private readonly uint[] _tile = new uint[16 * 16];

    public void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect)
    {
        // Background and foreground carry over from tile to tile within a rectangle.
        uint background = 0xFF000000u;
        uint foreground = 0xFF000000u;

        for (var ty = rect.Y; ty < rect.Bottom; ty += 16)
        {
            var th = Math.Min(16, rect.Bottom - ty);
            for (var tx = rect.X; tx < rect.Right; tx += 16)
            {
                var tw = Math.Min(16, rect.Right - tx);
                var subencoding = reader.ReadByte();

                if ((subencoding & Raw) != 0)
                {
                    var wire = _rawTile.AsSpan(0, tw * th * _converter.BytesPerPixel);
                    reader.ReadExactly(wire);
                    var raw = _rawPixels.AsSpan(0, tw * th);
                    _converter.ConvertRow(wire, raw);
                    framebuffer.Write(tx, ty, tw, th, raw);
                    continue;
                }

                if ((subencoding & BackgroundSpecified) != 0) background = reader.ReadPixel(_converter);
                if ((subencoding & ForegroundSpecified) != 0) foreground = reader.ReadPixel(_converter);

                var tile = _tile.AsSpan(0, tw * th);
                tile.Fill(background);

                if ((subencoding & AnySubrects) != 0)
                {
                    int count = reader.ReadByte();
                    var coloured = (subencoding & SubrectsColoured) != 0;
                    for (var i = 0; i < count; i++)
                    {
                        var colour = coloured ? reader.ReadPixel(_converter) : foreground;
                        var xy = reader.ReadByte();
                        var wh = reader.ReadByte();
                        int sx = xy >> 4, sy = xy & 0x0F;
                        int sw = (wh >> 4) + 1, sh = (wh & 0x0F) + 1;
                        var right = Math.Min(sx + sw, tw);
                        var bottom = Math.Min(sy + sh, th);
                        for (var row = sy; row < bottom; row++)
                        {
                            if (right > sx)
                                tile.Slice(row * tw + sx, right - sx).Fill(colour);
                        }
                    }
                }

                framebuffer.Write(tx, ty, tw, th, tile);
            }
        }
    }
}
