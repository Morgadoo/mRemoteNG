using System.Runtime.InteropServices;

namespace mRemoteNG.Protocols.Vnc.Rfb.Decoders;

/// <summary>Decodes one FramebufferUpdate rectangle of a given encoding into the framebuffer.</summary>
internal interface IRectangleDecoder
{
    void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect);
}

internal static class Pixels
{
    /// <summary>
    /// Reinterprets little-endian 32bpp wire pixels as BGRA and forces them opaque.
    /// All platforms .NET 10 supports for this app (x64, ARM64) are little-endian, so a cast suffices.
    /// </summary>
    public static Span<uint> ToOpaque(Span<byte> wire)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(wire);
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] |= 0xFF000000u;
        return pixels;
    }
}

/// <summary>Encoding 0: width × height uncompressed pixels.</summary>
internal sealed class RawDecoder : IRectangleDecoder
{
    private byte[] _row = [];

    public void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect)
    {
        var rowBytes = rect.Width * 4;
        if (_row.Length < rowBytes) _row = new byte[rowBytes];
        var row = _row.AsSpan(0, rowBytes);

        for (var y = 0; y < rect.Height; y++)
        {
            reader.ReadExactly(row);
            framebuffer.Write(rect.X, rect.Y + y, rect.Width, 1, Pixels.ToOpaque(row));
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
internal sealed class RreDecoder : IRectangleDecoder
{
    public void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect)
    {
        var count = reader.ReadUInt32();
        framebuffer.Fill(rect.X, rect.Y, rect.Width, rect.Height, reader.ReadPixel());

        for (uint i = 0; i < count; i++)
        {
            var colour = reader.ReadPixel();
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

/// <summary>Encoding 5: 16×16 tiles, each raw or background + (coloured) sub-rectangles.</summary>
internal sealed class HextileDecoder : IRectangleDecoder
{
    private const byte Raw = 1;
    private const byte BackgroundSpecified = 2;
    private const byte ForegroundSpecified = 4;
    private const byte AnySubrects = 8;
    private const byte SubrectsColoured = 16;

    private readonly byte[] _rawTile = new byte[16 * 16 * 4];
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
                    var wire = _rawTile.AsSpan(0, tw * th * 4);
                    reader.ReadExactly(wire);
                    framebuffer.Write(tx, ty, tw, th, Pixels.ToOpaque(wire));
                    continue;
                }

                if ((subencoding & BackgroundSpecified) != 0) background = reader.ReadPixel();
                if ((subencoding & ForegroundSpecified) != 0) foreground = reader.ReadPixel();

                var tile = _tile.AsSpan(0, tw * th);
                tile.Fill(background);

                if ((subencoding & AnySubrects) != 0)
                {
                    int count = reader.ReadByte();
                    var coloured = (subencoding & SubrectsColoured) != 0;
                    for (var i = 0; i < count; i++)
                    {
                        var colour = coloured ? reader.ReadPixel() : foreground;
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
