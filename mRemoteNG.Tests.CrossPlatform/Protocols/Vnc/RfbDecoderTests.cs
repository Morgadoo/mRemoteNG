using System.IO.Compression;
using FluentAssertions;
using mRemoteNG.Protocols.Vnc.Rfb;
using mRemoteNG.Protocols.Vnc.Rfb.Decoders;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

public class RfbDecoderTests
{
    private const byte Sentinel = 0xEE;

    private const uint Red = 0xFF0000;
    private const uint Green = 0x00FF00;
    private const uint Blue = 0x0000FF;
    private const uint White = 0xFFFFFF;
    private const uint Grey = 0x808080;

    /// <summary>Decodes <paramref name="data"/> and asserts the decoder consumed exactly those bytes.</summary>
    private static void Decode(IRectangleDecoder decoder, Framebuffer fb, RfbRect rect, byte[] data)
    {
        var reader = new RfbReader(new MemoryStream([.. data, Sentinel]));
        decoder.Decode(reader, fb, rect);
        reader.ReadByte().Should().Be(Sentinel, "the decoder must consume exactly the rectangle's bytes");
    }

    // ── Raw ────────────────────────────────────────────────────────────────

    [Fact]
    public void Raw_WritesPixelsInRowOrder_AndForcesOpaqueAlpha()
    {
        var fb = new Framebuffer(5, 4);
        var data = new RfbBytes();
        for (uint i = 0; i < 6; i++) data.Pixel(0x102030 * (i + 1));

        Decode(new RawDecoder(), fb, new RfbRect(1, 1, 3, 2), data);

        fb.GetPixel(1, 1).Should().Be(Colour.Of(0x102030));
        fb.GetPixel(3, 1).Should().Be(Colour.Of(0x102030 * 3));
        fb.GetPixel(1, 2).Should().Be(Colour.Of(0x102030 * 4));
        fb.GetPixel(3, 2).Should().Be(Colour.Of(0x102030 * 6));
        fb.GetPixel(0, 0).Should().Be(0u, "pixels outside the rectangle are untouched");
        fb.GetPixel(4, 3).Should().Be(0u);
    }

    // ── CopyRect ───────────────────────────────────────────────────────────

    [Fact]
    public void CopyRect_CopiesFromSourcePosition()
    {
        var fb = new Framebuffer(6, 4);
        fb.Fill(0, 0, 2, 2, Colour.Of(Red));
        fb.Fill(1, 1, 1, 1, Colour.Of(Blue));

        Decode(new CopyRectDecoder(), fb, new RfbRect(3, 2, 2, 2), new RfbBytes().U16(0).U16(0));

        fb.GetPixel(3, 2).Should().Be(Colour.Of(Red));
        fb.GetPixel(4, 3).Should().Be(Colour.Of(Blue));
        fb.GetPixel(1, 1).Should().Be(Colour.Of(Blue), "the source stays intact");
    }

    [Fact]
    public void CopyRect_HandlesOverlappingScrollDown()
    {
        var fb = new Framebuffer(1, 4);
        for (var y = 0; y < 4; y++) fb.Fill(0, y, 1, 1, (uint)y + 1);

        // Scroll rows 0-2 down by one.
        Decode(new CopyRectDecoder(), fb, new RfbRect(0, 1, 1, 3), new RfbBytes().U16(0).U16(0));

        Enumerable.Range(0, 4).Select(y => fb.GetPixel(0, y)).Should().Equal(1u, 1u, 2u, 3u);
    }

    [Fact]
    public void CopyRect_HandlesOverlappingScrollLeft()
    {
        var fb = new Framebuffer(4, 1);
        for (var x = 0; x < 4; x++) fb.Fill(x, 0, 1, 1, (uint)x + 1);

        Decode(new CopyRectDecoder(), fb, new RfbRect(0, 0, 3, 1), new RfbBytes().U16(1).U16(0));

        Enumerable.Range(0, 4).Select(x => fb.GetPixel(x, 0)).Should().Equal(2u, 3u, 4u, 4u);
    }

    // ── RRE ────────────────────────────────────────────────────────────────

    [Fact]
    public void Rre_FillsBackgroundThenSubrectangles()
    {
        var fb = new Framebuffer(8, 6);
        var data = new RfbBytes()
            .U32(2).Pixel(Grey)
            .Pixel(Red).U16(0).U16(0).U16(2).U16(1)
            .Pixel(Blue).U16(3).U16(2).U16(1).U16(2);

        Decode(new RreDecoder(), fb, new RfbRect(2, 1, 5, 4), data);

        fb.GetPixel(2, 1).Should().Be(Colour.Of(Red));
        fb.GetPixel(3, 1).Should().Be(Colour.Of(Red));
        fb.GetPixel(4, 1).Should().Be(Colour.Of(Grey));
        fb.GetPixel(5, 3).Should().Be(Colour.Of(Blue));
        fb.GetPixel(5, 4).Should().Be(Colour.Of(Blue));
        fb.GetPixel(6, 4).Should().Be(Colour.Of(Grey));
        fb.GetPixel(1, 1).Should().Be(0u);
        fb.GetPixel(7, 5).Should().Be(0u);
    }

    // ── Hextile ────────────────────────────────────────────────────────────

    [Fact]
    public void Hextile_DecodesAllTileKinds_AndCarriesBackgroundAcrossTiles()
    {
        // 20×18 → tiles (16×16) (4×16) / (16×2) (4×2).
        var fb = new Framebuffer(20, 18);
        var data = new RfbBytes()
            // Tile 1: background grey, foreground red, one foreground subrect at (2,3) 4×5.
            .U8(2 | 4 | 8).Pixel(Grey).Pixel(Red).U8(1).U8(2 << 4 | 3).U8(3 << 4 | 4);
        // Tile 2: raw 4×16, every pixel distinct.
        data.U8(1);
        for (uint i = 0; i < 4 * 16; i++) data.Pixel(i + 1);
        // Tile 3: no background given (inherits grey), two coloured subrects.
        data.U8(8 | 16).U8(2)
            .Pixel(Blue).U8(0 << 4 | 0).U8(0 << 4 | 0)
            .Pixel(White).U8(15 << 4 | 1).U8(0 << 4 | 0);
        // Tile 4: solid green background.
        data.U8(2).Pixel(Green);

        Decode(new HextileDecoder(), fb, new RfbRect(0, 0, 20, 18), data);

        // Tile 1
        fb.GetPixel(0, 0).Should().Be(Colour.Of(Grey));
        fb.GetPixel(2, 3).Should().Be(Colour.Of(Red));
        fb.GetPixel(5, 7).Should().Be(Colour.Of(Red));
        fb.GetPixel(6, 7).Should().Be(Colour.Of(Grey));
        fb.GetPixel(5, 8).Should().Be(Colour.Of(Grey));
        // Tile 2 (raw, origin 16,0)
        fb.GetPixel(16, 0).Should().Be(Colour.Of(1));
        fb.GetPixel(19, 0).Should().Be(Colour.Of(4));
        fb.GetPixel(16, 1).Should().Be(Colour.Of(5));
        fb.GetPixel(19, 15).Should().Be(Colour.Of(64));
        // Tile 3 (origin 0,16)
        fb.GetPixel(0, 16).Should().Be(Colour.Of(Blue));
        fb.GetPixel(15, 17).Should().Be(Colour.Of(White));
        fb.GetPixel(1, 16).Should().Be(Colour.Of(Grey), "background carries over from tile 1");
        // Tile 4 (origin 16,16)
        fb.GetPixel(16, 16).Should().Be(Colour.Of(Green));
        fb.GetPixel(19, 17).Should().Be(Colour.Of(Green));
    }

    // ── ZRLE ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Compresses each tile payload into one zlib stream with a sync flush after each, like a server does per
    /// rectangle, and returns the per-rectangle wire data (u32 length + compressed bytes).
    /// </summary>
    private static List<byte[]> CompressZrle(params byte[][] payloads)
    {
        var output = new MemoryStream();
        using var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true);
        var rects = new List<byte[]>();
        long previous = 0;
        foreach (var payload in payloads)
        {
            zlib.Write(payload);
            zlib.Flush();
            var compressed = output.ToArray()[(int)previous..];
            previous = output.Length;
            rects.Add(new RfbBytes().U32((uint)compressed.Length).Raw(compressed));
        }
        return rects;
    }

    [Fact]
    public void Zrle_DecodesEverySubencoding_WithOneZlibStreamAcrossRectangles()
    {
        var fb = new Framebuffer(70, 12);

        // Rect A (0,0,70,3): tile 64×3 plain RLE; tile 6×3 raw.
        var a = new RfbBytes()
            .U8(128).CPixel(Red).U8(100 - 1).CPixel(Green).U8(92 - 1);
        a.U8(0);
        for (uint i = 0; i < 18; i++) a.CPixel(0x010203 * (i + 1));

        // Rect B (0,3,64,8): palette RLE with 3 colours; run of 300 uses a 255 continuation byte.
        var b = new RfbBytes()
            .U8(128 + 3).CPixel(Red).CPixel(Green).CPixel(Blue)
            .U8(0x80 | 1).U8(255).U8(300 - 1 - 255)
            .U8(2)
            .U8(0x80 | 0).U8(211 - 1);

        // Rect C (64,3,6,4): solid.
        var c = new RfbBytes().U8(1).CPixel(Blue);

        // Rect D (64,7,6,4): packed palette of 5 colours, 4 bits per index, rows padded to a byte.
        var d = new RfbBytes().U8(5).CPixel(Red).CPixel(Green).CPixel(Blue).CPixel(White).CPixel(Grey);
        for (var row = 0; row < 4; row++) d.U8(0x01).U8(0x23).U8(0x40);   // indices 0 1 2 3 4 0

        // Rect E (0,11,70,1): 64×1 two-colour (1 bit) tile, then a 6×1 three-colour (2 bit) tile.
        var e = new RfbBytes().U8(2).CPixel(White).CPixel(Red)
            .U8(0b1000_0000).U8(0).U8(0).U8(0).U8(0).U8(0).U8(0).U8(0b0000_0001);
        e.U8(3).CPixel(Red).CPixel(Green).CPixel(Blue).U8(0b00_01_10_00).U8(0b01_10_0000);

        var wire = CompressZrle(a, b, c, d, e);
        var decoder = new ZrleDecoder();
        Decode(decoder, fb, new RfbRect(0, 0, 70, 3), wire[0]);
        Decode(decoder, fb, new RfbRect(0, 3, 64, 8), wire[1]);
        Decode(decoder, fb, new RfbRect(64, 3, 6, 4), wire[2]);
        Decode(decoder, fb, new RfbRect(64, 7, 6, 4), wire[3]);
        Decode(decoder, fb, new RfbRect(0, 11, 70, 1), wire[4]);

        // A: 100 red pixels then green (row-major in the 64-wide tile).
        fb.GetPixel(0, 0).Should().Be(Colour.Of(Red));
        fb.GetPixel(35, 1).Should().Be(Colour.Of(Red));   // index 99
        fb.GetPixel(36, 1).Should().Be(Colour.Of(Green)); // index 100
        fb.GetPixel(63, 2).Should().Be(Colour.Of(Green));
        fb.GetPixel(64, 0).Should().Be(Colour.Of(0x010203));
        fb.GetPixel(69, 2).Should().Be(Colour.Of(0x010203 * 18));

        // B: 300 green, 1 blue, 211 red.
        fb.GetPixel(0, 3).Should().Be(Colour.Of(Green));
        fb.GetPixel(299 % 64, 3 + 299 / 64).Should().Be(Colour.Of(Green));
        fb.GetPixel(300 % 64, 3 + 300 / 64).Should().Be(Colour.Of(Blue));
        fb.GetPixel(301 % 64, 3 + 301 / 64).Should().Be(Colour.Of(Red));
        fb.GetPixel(63, 10).Should().Be(Colour.Of(Red));

        // C and D
        fb.GetPixel(64, 3).Should().Be(Colour.Of(Blue));
        fb.GetPixel(69, 6).Should().Be(Colour.Of(Blue));
        Enumerable.Range(64, 6).Select(x => fb.GetPixel(x, 9))
            .Should().Equal(new[] { Red, Green, Blue, White, Grey, Red }.Select(Colour.Of));

        // E
        fb.GetPixel(0, 11).Should().Be(Colour.Of(Red));
        fb.GetPixel(1, 11).Should().Be(Colour.Of(White));
        fb.GetPixel(63, 11).Should().Be(Colour.Of(Red));
        Enumerable.Range(64, 6).Select(x => fb.GetPixel(x, 11))
            .Should().Equal(new[] { Red, Green, Blue, Red, Green, Blue }.Select(Colour.Of));
    }

    [Fact]
    public void Zrle_InvalidSubencoding_Throws()
    {
        var wire = CompressZrle(new RfbBytes().U8(17));
        var act = () => new ZrleDecoder().Decode(new RfbReader(new MemoryStream(wire[0])), new Framebuffer(4, 4), new RfbRect(0, 0, 4, 4));

        act.Should().Throw<RfbProtocolException>();
    }

    // ── Framebuffer ────────────────────────────────────────────────────────

    [Fact]
    public void Framebuffer_CoalescesDirtyRectangles()
    {
        var fb = new Framebuffer(100, 100);
        fb.TakeDirty().Should().Be(new RfbRect(0, 0, 100, 100), "a new framebuffer is entirely dirty");
        fb.TakeDirty().IsEmpty.Should().BeTrue();

        fb.MarkDirty(new RfbRect(10, 10, 5, 5));
        fb.MarkDirty(new RfbRect(50, 20, 10, 40));
        fb.MarkDirty(new RfbRect(95, 95, 20, 20));

        fb.TakeDirty().Should().Be(new RfbRect(10, 10, 90, 90));
    }

    [Fact]
    public void Framebuffer_Resize_MarksEverythingDirty()
    {
        var fb = new Framebuffer(10, 10);
        fb.TakeDirty();

        fb.Resize(20, 5);

        fb.Width.Should().Be(20);
        fb.Pixels.Length.Should().Be(100);
        fb.TakeDirty().Should().Be(new RfbRect(0, 0, 20, 5));
    }
}
