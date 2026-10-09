using System.IO.Compression;
using FluentAssertions;
using mRemoteNG.Protocols.Vnc.Rfb;
using mRemoteNG.Protocols.Vnc.Rfb.Decoders;
using SkiaSharp;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary>Tight, Zlib and CoRRE decoders and the pixel-format conversion they share.</summary>
public class RfbTightDecoderTests
{
    private const byte Sentinel = 0xEE;
    private static readonly PixelConverter Rgb565 = new(PixelFormat.Rgb565);
    private static readonly PixelConverter Bgr233 = new(PixelFormat.Bgr233);

    private static void Decode(IRectangleDecoder decoder, Framebuffer fb, RfbRect rect, byte[] data)
    {
        var reader = new RfbReader(new MemoryStream([.. data, Sentinel]));
        decoder.Decode(reader, fb, rect);
        reader.ReadByte().Should().Be(Sentinel, "the decoder must consume exactly the rectangle's bytes");
    }

    /// <summary>One zlib stream; each payload is sync-flushed and returned as its compressed chunk.</summary>
    private sealed class ZlibWriter : IDisposable
    {
        private readonly MemoryStream _output = new();
        private readonly ZLibStream _zlib;
        private long _previous;

        public ZlibWriter() => _zlib = new ZLibStream(_output, CompressionLevel.Optimal, leaveOpen: true);

        public byte[] Chunk(byte[] payload)
        {
            _zlib.Write(payload);
            _zlib.Flush();
            var chunk = _output.ToArray()[(int)_previous..];
            _previous = _output.Length;
            return chunk;
        }

        public void Dispose() => _zlib.Dispose();
    }

    /// <summary>Tight compact length (1-3 bytes, 7 bits each, least significant first).</summary>
    private static RfbBytes CompactLength(RfbBytes bytes, int length)
    {
        if (length < 0x80) return bytes.U8(length);
        if (length < 0x4000) return bytes.U8(length & 0x7F | 0x80).U8(length >> 7);
        return bytes.U8(length & 0x7F | 0x80).U8(length >> 7 & 0x7F | 0x80).U8(length >> 14);
    }

    // ── Pixel formats ──────────────────────────────────────────────────────

    [Theory]
    [InlineData(0xF800u, 0xFF0000u)]
    [InlineData(0x07E0u, 0x00FF00u)]
    [InlineData(0x001Fu, 0x0000FFu)]
    [InlineData(0xFFFFu, 0xFFFFFFu)]
    [InlineData(0x8410u, 0x848284u)] // 16/31, 32/63, 16/31 of full scale
    public void Rgb565_ScalesComponentsToEightBits(uint value, uint rgb)
    {
        Rgb565.ToBgra(value).Should().Be(Colour.Of(rgb));
        Rgb565.ReadPixel([(byte)value, (byte)(value >> 8)]).Should().Be(Colour.Of(rgb), "16bpp pixels are little-endian on the wire");
    }

    [Theory]
    [InlineData(0x07, 0xFF0000u)] // red: bits 0-2
    [InlineData(0x38, 0x00FF00u)] // green: bits 3-5
    [InlineData(0xC0, 0x0000FFu)] // blue: bits 6-7
    [InlineData(0x00, 0x000000u)]
    public void Bgr233_ScalesComponentsToEightBits(byte value, uint rgb) =>
        Bgr233.ReadPixel([value]).Should().Be(Colour.Of(rgb));

    [Fact]
    public void BigEndianFormats_AreReadMostSignificantByteFirst()
    {
        var converter = new PixelConverter(PixelFormat.Rgb565 with { BigEndian = true });
        converter.ReadPixel([0xF8, 0x00]).Should().Be(Colour.Of(0xFF0000));
    }

    [Fact]
    public void CompactAndTightPixels_HaveTheirOwnLayouts()
    {
        var bgra = PixelConverter.Bgra32;
        bgra.CompactBytesPerPixel.Should().Be(3);
        bgra.ReadCompactPixel([0x30, 0x20, 0x10]).Should().Be(Colour.Of(0x102030), "ZRLE CPIXEL keeps wire order B, G, R");
        bgra.TightBytesPerPixel.Should().Be(3);
        bgra.ReadTightPixel([0x10, 0x20, 0x30]).Should().Be(Colour.Of(0x102030), "Tight TPIXEL is R, G, B");
        Rgb565.CompactBytesPerPixel.Should().Be(2);
        Rgb565.TightBytesPerPixel.Should().Be(2);
    }

    [Fact]
    public void Raw_At16And8Bpp_ConvertsEveryPixel()
    {
        var fb = new Framebuffer(3, 1);
        Decode(new RawDecoder(Rgb565), fb, new RfbRect(0, 0, 3, 1), [0x00, 0xF8, 0xE0, 0x07, 0x1F, 0x00]);
        Enumerable.Range(0, 3).Select(x => fb.GetPixel(x, 0)).Should().Equal(Colour.Of(0xFF0000), Colour.Of(0x00FF00), Colour.Of(0x0000FF));

        Decode(new RawDecoder(Bgr233), fb, new RfbRect(0, 0, 3, 1), [0x38, 0xC0, 0x07]);
        Enumerable.Range(0, 3).Select(x => fb.GetPixel(x, 0)).Should().Equal(Colour.Of(0x00FF00), Colour.Of(0x0000FF), Colour.Of(0xFF0000));
    }

    [Fact]
    public void Zrle_At16Bpp_UsesTwoBytePixels()
    {
        using var zlib = new ZlibWriter();
        var tile = new RfbBytes().U8(1).Raw(0x00, 0xF8); // solid red
        var chunk = zlib.Chunk(tile);
        var fb = new Framebuffer(4, 4);

        Decode(new ZrleDecoder(Rgb565), fb, new RfbRect(0, 0, 4, 4), new RfbBytes().U32((uint)chunk.Length).Raw(chunk));

        fb.Pixels.Should().OnlyContain(p => p == Colour.Of(0xFF0000));
    }

    // ── CoRRE and Zlib ─────────────────────────────────────────────────────

    [Fact]
    public void CoRre_FillsBackgroundThenByteCoordinateSubrectangles()
    {
        var fb = new Framebuffer(10, 10);
        var data = new RfbBytes().U32(2).Pixel(0x0000FF)
            .Pixel(0xFF0000).U8(1).U8(1).U8(2).U8(2)
            .Pixel(0x00FF00).U8(3).U8(0).U8(9).U8(1); // overhangs the 5-wide rectangle: clipped

        Decode(new CoRreDecoder(), fb, new RfbRect(2, 2, 5, 4), data);

        fb.GetPixel(2, 2).Should().Be(Colour.Of(0x0000FF));
        fb.GetPixel(3, 3).Should().Be(Colour.Of(0xFF0000));
        fb.GetPixel(4, 4).Should().Be(Colour.Of(0xFF0000));
        fb.GetPixel(5, 2).Should().Be(Colour.Of(0x00FF00));
        fb.GetPixel(6, 2).Should().Be(Colour.Of(0x00FF00));
        fb.GetPixel(7, 2).Should().Be(0u, "sub-rectangles are clipped to their rectangle");
    }

    [Fact]
    public void Zlib_DecodesRawPixels_WithOneStreamAcrossRectangles()
    {
        using var zlib = new ZlibWriter();
        var first = new RfbBytes();
        for (uint i = 0; i < 6; i++) first.Pixel(0x010101 * (i + 1));
        var second = new RfbBytes().Pixel(0xABCDEF).Pixel(0x123456);
        var chunk1 = zlib.Chunk(first);
        var chunk2 = zlib.Chunk(second);
        var fb = new Framebuffer(4, 4);
        var decoder = new ZlibDecoder();

        Decode(decoder, fb, new RfbRect(0, 0, 3, 2), new RfbBytes().U32((uint)chunk1.Length).Raw(chunk1));
        Decode(decoder, fb, new RfbRect(0, 3, 2, 1), new RfbBytes().U32((uint)chunk2.Length).Raw(chunk2));

        fb.GetPixel(0, 0).Should().Be(Colour.Of(0x010101));
        fb.GetPixel(2, 1).Should().Be(Colour.Of(0x060606));
        fb.GetPixel(0, 3).Should().Be(Colour.Of(0xABCDEF));
        fb.GetPixel(1, 3).Should().Be(Colour.Of(0x123456));
    }

    // ── Tight ──────────────────────────────────────────────────────────────

    [Fact]
    public void Tight_Fill_UsesRgbOrderedTPixel()
    {
        var fb = new Framebuffer(4, 4);
        Decode(new TightDecoder(), fb, new RfbRect(1, 1, 2, 2), new RfbBytes().U8(0x80).Raw(0x11, 0x22, 0x33));

        fb.GetPixel(1, 1).Should().Be(Colour.Of(0x112233));
        fb.GetPixel(2, 2).Should().Be(Colour.Of(0x112233));
        fb.GetPixel(0, 0).Should().Be(0u);
    }

    [Fact]
    public void Tight_Fill_At16Bpp_UsesAFullPixel()
    {
        var fb = new Framebuffer(2, 2);
        Decode(new TightDecoder(Rgb565), fb, new RfbRect(0, 0, 2, 2), new RfbBytes().U8(0x80).Raw(0x1F, 0x00));
        fb.Pixels.Should().OnlyContain(p => p == Colour.Of(0x0000FF));
    }

    [Fact]
    public void Tight_Copy_SendsSmallDataUncompressed()
    {
        // 3 pixels × 3 bytes = 9 < 12: raw bytes, no length, no zlib.
        var fb = new Framebuffer(3, 1);
        Decode(new TightDecoder(), fb, new RfbRect(0, 0, 3, 1),
            new RfbBytes().U8(0x00).Raw(0xFF, 0, 0, 0, 0xFF, 0, 0, 0, 0xFF));

        Enumerable.Range(0, 3).Select(x => fb.GetPixel(x, 0))
            .Should().Equal(Colour.Of(0xFF0000), Colour.Of(0x00FF00), Colour.Of(0x0000FF));
    }

    [Fact]
    public void Tight_Copy_KeepsEachZlibStreamAcrossRectangles_UntilReset()
    {
        var decoder = new TightDecoder();
        var fb = new Framebuffer(4, 4);
        var stream2 = new ZlibWriter();

        byte[] Rgb(uint rgb, int count) =>
            Enumerable.Range(0, count).SelectMany(_ => new[] { (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb }).ToArray();

        // Stream 2, explicit copy filter (bit 6 set): control = 0x40 | 2 << 4 = 0x60.
        var chunk = stream2.Chunk(Rgb(0x102030, 8));
        Decode(decoder, fb, new RfbRect(0, 0, 4, 2), CompactLength(new RfbBytes().U8(0x60).U8(0), chunk.Length).Raw(chunk));
        // Same stream continues (no reset bit), implicit copy filter: control = 2 << 4 = 0x20.
        chunk = stream2.Chunk(Rgb(0x405060, 8));
        Decode(decoder, fb, new RfbRect(0, 2, 4, 2), CompactLength(new RfbBytes().U8(0x20), chunk.Length).Raw(chunk));

        fb.GetPixel(3, 1).Should().Be(Colour.Of(0x102030));
        fb.GetPixel(0, 2).Should().Be(Colour.Of(0x405060));

        // The server restarts stream 2 and says so with reset bit 2.
        stream2.Dispose();
        using var restarted = new ZlibWriter();
        chunk = restarted.Chunk(Rgb(0x708090, 16));
        Decode(decoder, fb, new RfbRect(0, 0, 4, 4), CompactLength(new RfbBytes().U8(0x24), chunk.Length).Raw(chunk));
        fb.Pixels.Should().OnlyContain(p => p == Colour.Of(0x708090));
        decoder.Statistics.Copy.Should().Be(3);
    }

    [Fact]
    public void Tight_Palette_TwoColoursUseOneBitPerPixel_RowsPadded()
    {
        // 10×2: stride 2 bytes per row = 4 bytes < 12, so uncompressed.
        var fb = new Framebuffer(10, 2);
        var data = new RfbBytes().U8(0x40).U8(1).U8(2 - 1).Raw(0, 0, 0).Raw(0xFF, 0xFF, 0xFF)
            .U8(0b1010_0000).U8(0b0100_0000)  // row 0: x0, x2, x9 white
            .U8(0b0000_0000).U8(0b1000_0000); // row 1: x8 white
        Decode(new TightDecoder(), fb, new RfbRect(0, 0, 10, 2), data);

        var white = Colour.Of(0xFFFFFF);
        var black = Colour.Of(0);
        Enumerable.Range(0, 10).Select(x => fb.GetPixel(x, 0))
            .Should().Equal(white, black, white, black, black, black, black, black, black, white);
        Enumerable.Range(0, 10).Select(x => fb.GetPixel(x, 1))
            .Should().Equal(black, black, black, black, black, black, black, black, white, black);
    }

    [Fact]
    public void Tight_Palette_ManyColoursUseOneBytePerPixel_Compressed()
    {
        using var zlib = new ZlibWriter();
        var indices = Enumerable.Range(0, 16).Select(i => (byte)(i % 3)).ToArray();
        var chunk = zlib.Chunk(indices);
        var data = CompactLength(new RfbBytes().U8(0x40).U8(1).U8(3 - 1)
            .Raw(0xFF, 0, 0).Raw(0, 0xFF, 0).Raw(0, 0, 0xFF), chunk.Length).Raw(chunk);
        var fb = new Framebuffer(4, 4);

        Decode(new TightDecoder(), fb, new RfbRect(0, 0, 4, 4), data);

        fb.GetPixel(0, 0).Should().Be(Colour.Of(0xFF0000));
        fb.GetPixel(1, 0).Should().Be(Colour.Of(0x00FF00));
        fb.GetPixel(2, 0).Should().Be(Colour.Of(0x0000FF));
        fb.GetPixel(3, 3).Should().Be(Colour.Of(0xFF0000)); // index 15 % 3 = 0
    }

    [Fact]
    public void Tight_PaletteIndexOutOfRange_Throws()
    {
        var data = new RfbBytes().U8(0x40).U8(1).U8(3 - 1).Raw(0, 0, 0).Raw(0, 0, 0).Raw(0, 0, 0).Raw(0, 1, 7, 2);
        var act = () => Decode(new TightDecoder(), new Framebuffer(4, 1), new RfbRect(0, 0, 4, 1), data);
        act.Should().Throw<RfbProtocolException>();
    }

    /// <summary>Encodes components with the Tight gradient predictor (the server side of the filter).</summary>
    private static byte[] GradientEncode(int[,,] components, int[] max, Func<int[], byte[]> pack)
    {
        int height = components.GetLength(0), width = components.GetLength(1);
        var output = new List<byte>();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var residual = new int[3];
                for (var c = 0; c < 3; c++)
                {
                    var left = x > 0 ? components[y, x - 1, c] : 0;
                    var upper = y > 0 ? components[y - 1, x, c] : 0;
                    var upperLeft = x > 0 && y > 0 ? components[y - 1, x - 1, c] : 0;
                    var predicted = Math.Clamp(left + upper - upperLeft, 0, max[c]);
                    residual[c] = (components[y, x, c] - predicted) & max[c];
                }
                output.AddRange(pack(residual));
            }
        }
        return output.ToArray();
    }

    [Fact]
    public void Tight_Gradient_24Bit_ReconstructsEveryPixel()
    {
        var random = new Random(7);
        const int width = 9, height = 5;
        var components = new int[height, width, 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var c = 0; c < 3; c++)
                    components[y, x, c] = random.Next(256);

        using var zlib = new ZlibWriter();
        var chunk = zlib.Chunk(GradientEncode(components, [255, 255, 255], r => [(byte)r[0], (byte)r[1], (byte)r[2]]));
        var fb = new Framebuffer(width, height);
        var decoder = new TightDecoder();

        Decode(decoder, fb, new RfbRect(0, 0, width, height), CompactLength(new RfbBytes().U8(0x40).U8(2), chunk.Length).Raw(chunk));

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                fb.GetPixel(x, y).Should().Be(PixelConverter.FromRgb((byte)components[y, x, 0], (byte)components[y, x, 1], (byte)components[y, x, 2]));
        decoder.Statistics.Gradient.Should().Be(1);
    }

    [Fact]
    public void Tight_Gradient_16Bit_PredictsInTheFormatsComponents()
    {
        var random = new Random(11);
        const int width = 6, height = 4;
        int[] max = [31, 63, 31];
        var components = new int[height, width, 3];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var c = 0; c < 3; c++)
                    components[y, x, c] = random.Next(max[c] + 1);

        using var zlib = new ZlibWriter();
        var encoded = GradientEncode(components, max, r =>
        {
            var v = r[0] << 11 | r[1] << 5 | r[2];
            return [(byte)v, (byte)(v >> 8)];
        });
        var chunk = zlib.Chunk(encoded);
        var fb = new Framebuffer(width, height);

        Decode(new TightDecoder(Rgb565), fb, new RfbRect(0, 0, width, height), CompactLength(new RfbBytes().U8(0x40).U8(2), chunk.Length).Raw(chunk));

        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                fb.GetPixel(x, y).Should().Be(Rgb565.ToBgra((uint)(components[y, x, 0] << 11 | components[y, x, 1] << 5 | components[y, x, 2])));
    }

    [Fact]
    public void Tight_Jpeg_DecodesTheImage()
    {
        const int width = 16, height = 8;
        using var bitmap = new SKBitmap(width, height);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, x < 8 ? new SKColor(200, 30, 30) : new SKColor(30, 30, 200));
        using var image = SKImage.FromBitmap(bitmap);
        var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 95).ToArray();

        var fb = new Framebuffer(20, 10);
        var decoder = new TightDecoder();
        Decode(decoder, fb, new RfbRect(2, 1, width, height), CompactLength(new RfbBytes().U8(0x90), jpeg.Length).Raw(jpeg));

        static bool Near(uint pixel, int r, int g, int b) =>
            Math.Abs((int)(pixel >> 16 & 0xFF) - r) < 12 && Math.Abs((int)(pixel >> 8 & 0xFF) - g) < 12
            && Math.Abs((int)(pixel & 0xFF) - b) < 12 && (pixel & 0xFF000000u) == 0xFF000000u;
        Near(fb.GetPixel(3, 2), 200, 30, 30).Should().BeTrue();
        Near(fb.GetPixel(16, 7), 30, 30, 200).Should().BeTrue();
        fb.GetPixel(0, 0).Should().Be(0u);
        decoder.Statistics.Jpeg.Should().Be(1);
    }

    [Fact]
    public void Tight_InvalidCompressionControl_Throws()
    {
        var act = () => Decode(new TightDecoder(), new Framebuffer(1, 1), new RfbRect(0, 0, 1, 1), [0xB0]);
        act.Should().Throw<RfbProtocolException>();
    }

    [Theory]
    [InlineData(new byte[] { 0x05 }, 5)]
    [InlineData(new byte[] { 0x90, 0x4E }, 10000)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0x7F }, 2097151)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF }, 4194303)]
    public void Tight_CompactLength(byte[] bytes, int expected) =>
        TightDecoder.ReadCompactLength(new RfbReader(new MemoryStream(bytes))).Should().Be(expected);
}
