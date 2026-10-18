using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using FluentAssertions;
using mRemoteNG.Protocols.Vnc.Rfb;
using mRemoteNG.Protocols.Vnc.Rfb.Decoders;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary>UltraVNC encodings: Ultra (LZO1X) and ZlibHex.</summary>
public class RfbUltraDecoderTests
{
    private const byte Sentinel = 0xEE;
    private const uint Red = 0xFF0000;
    private const uint Green = 0x00FF00;
    private const uint Blue = 0x0000FF;
    private const uint Grey = 0x808080;

    private static void Decode(IRectangleDecoder decoder, Framebuffer fb, RfbRect rect, byte[] data)
    {
        var reader = new RfbReader(new MemoryStream([.. data, Sentinel]));
        decoder.Decode(reader, fb, rect);
        reader.ReadByte().Should().Be(Sentinel, "the decoder must consume exactly the rectangle's bytes");
    }

    /// <summary>A literal-only LZO1X stream (at most 238 bytes) followed by the end-of-stream marker.</summary>
    private static byte[] LzoLiteral(byte[] data) => [(byte)(17 + data.Length), .. data, 0x11, 0, 0];

    // ── LZO1X ──────────────────────────────────────────────────────────────

    private static byte[] TextSample() =>
    [
        .. Enumerable.Repeat((byte)'A', 40),
        .. Enumerable.Range(0, 64).SelectMany(i => Encoding.ASCII.GetBytes($"row {i:D2}: {i * i * 7 % 1000:D3} pixels;")),
    ];

    /// <summary><see cref="TextSample"/> compressed by liblzo2's <c>lzo1x_1_compress</c>.</summary>
    private const string TextSampleLzo =
        "AkFBQUFBIAIQAAABcm93IDAwOiAwMDAgcGl4ZWxzO4kCMWkCNytIAAMyOiAwMjgrSAADMzogMDYzK0gAAzQ6IDExMitIAAM1OiAxNzUrSAACNjogMjUslAACNzogMzQsLAECODogNDQsxAECOTogNTYrXAICMTA6IDcs9AIDMTE6IDg0LJQAdRcwKywBAzEzOiAxOCvEAQMxNDogMzcrXAICMTU6IDUs9AIDMTY6IDc5LJUAN3AjLCwBAjg6IDI2LMQBdRcyK1wCAjIwOiA4LPUCMm0vOCyUAAIyOiAzOCsuATIzeB4rxAEDMjQ6IDAzK14CMjVgGivtBTJ0Fy2UAAI3OiAxMCwsAWwvLcQBATk6IDgsXAICMzA6IDMs9AICMzE6IDcsjAMCMzI6IDEsJAQDMzM6IDYyK8UBM3QXLFUFMzDsBQIzNjogMCyEBgIzNzogNSweBzM4YBorvAQCMzk6IDYsTAgCNDA6IDIs9QI0dBcsfQk0bC8sFAoCNDM6IDksrAoCNDQ6IDUsRQs0MNwLAjQ2OiA4LHQMAjQ3OiA0LA0NNHQXLKUNNG0vMCtMCAI1MDogNSz2AjUxYBoslAABMjogOSwtATVsLyzEAQI1NDogNCxdAjUw9AICNTY6IDksjAMCNTc6IDcsJAQCNTg6IDUsvAQCNTk6IDMsVQU2MO0FNnxeLIUGNnQXKtQODncgNjM6IDc4MyBwaXhlbHM7EQAA";

    [Fact]
    public void Lzo_DecompressesTheOutputOfLiblzo()
    {
        var expected = TextSample();
        var output = new byte[expected.Length];

        Lzo1x.Decompress(Convert.FromBase64String(TextSampleLzo), output).Should().Be(expected.Length);

        output.Should().Equal(expected);
    }

    [Fact]
    public void Lzo_DecompressesALeadingLiteralRun()
    {
        var output = new byte[5];

        Lzo1x.Decompress(LzoLiteral("hello"u8.ToArray()), output).Should().Be(5);

        Encoding.ASCII.GetString(output).Should().Be("hello");
    }

    [Fact]
    public void Lzo_RejectsTruncatedOrOversizedData()
    {
        var compressed = Convert.FromBase64String(TextSampleLzo);

        var truncated = () => Lzo1x.Decompress(compressed.AsSpan(0, compressed.Length / 2), new byte[TextSample().Length]);
        var tooSmall = () => Lzo1x.Decompress(compressed, new byte[100]);
        var trailing = () => Lzo1x.Decompress([.. LzoLiteral("hello"u8.ToArray()), 0], new byte[5]);
        var empty = () => Lzo1x.Decompress([], new byte[5]);

        truncated.Should().Throw<RfbProtocolException>();
        tooSmall.Should().Throw<RfbProtocolException>();
        trailing.Should().Throw<RfbProtocolException>();
        empty.Should().Throw<RfbProtocolException>();
    }

    [Fact]
    public void Lzo_RejectsAMatchBeforeTheStartOfTheOutput()
    {
        // A literal byte, then an M2 match 9 bytes back.
        var act = () => Lzo1x.Decompress([18, (byte)'x', 64 | (1 << 2), 1, 0x11, 0, 0], new byte[16]);

        act.Should().Throw<RfbProtocolException>().WithMessage("*before the start*");
    }

    [SkippableFact]
    public void Lzo_RoundTripsWithLiblzo_IncludingFarMatches()
    {
        Skip.IfNot(LibLzo.IsAvailable, "liblzo2 is not installed");
        var random = new Random(7);
        var data = new List<byte>();
        data.AddRange(Enumerable.Repeat((byte)'A', 300));
        for (var block = 0; block < 12; block++)
        {
            var noise = new byte[3000 + block * 500];
            random.NextBytes(noise);
            data.AddRange(noise);
            // Repeats at short, medium (> 2 KiB) and long (> 16 KiB) distances.
            data.AddRange(data.Skip(data.Count - 50).Take(6));
            data.AddRange(data.Skip(Math.Max(0, data.Count - 2500)).Take(40));
            data.AddRange(data.Skip(Math.Max(0, data.Count - 20000)).Take(200));
        }
        var input = data.ToArray();

        var output = new byte[input.Length];
        Lzo1x.Decompress(LibLzo.Compress(input), output).Should().Be(input.Length);

        output.Should().Equal(input);
    }

    // ── Ultra ──────────────────────────────────────────────────────────────

    [Fact]
    public void Ultra_DecompressesRawPixels()
    {
        var pixels = new RfbBytes();
        for (uint i = 0; i < 12; i++) pixels.Pixel(0x010203 * (i + 1));
        var lzo = LzoLiteral(pixels);
        var fb = new Framebuffer(6, 5);

        Decode(new UltraDecoder(), fb, new RfbRect(1, 1, 4, 3), new RfbBytes().U32((uint)lzo.Length).Raw(lzo));

        fb.GetPixel(1, 1).Should().Be(Colour.Of(0x010203));
        fb.GetPixel(4, 1).Should().Be(Colour.Of(0x010203 * 4));
        fb.GetPixel(1, 3).Should().Be(Colour.Of(0x010203 * 9));
        fb.GetPixel(4, 3).Should().Be(Colour.Of(0x010203 * 12));
        fb.GetPixel(0, 0).Should().Be(0u);
    }

    [Fact]
    public void Ultra_EmptyPayload_LeavesTheRectangleUnchanged()
    {
        var fb = new Framebuffer(4, 4);
        fb.Fill(0, 0, 4, 4, Colour.Of(Grey));

        Decode(new UltraDecoder(), fb, new RfbRect(0, 0, 4, 4), new RfbBytes().U32(0));

        fb.GetPixel(2, 2).Should().Be(Colour.Of(Grey));
    }

    [Fact]
    public void Ultra_WrongDecompressedSize_Throws()
    {
        var lzo = LzoLiteral(new RfbBytes().Pixel(Red).Pixel(Green));
        var data = new RfbBytes().U32((uint)lzo.Length).Raw(lzo).ToArray();

        var act = () => new UltraDecoder().Decode(new RfbReader(new MemoryStream(data)), new Framebuffer(4, 4), new RfbRect(0, 0, 2, 2));

        act.Should().Throw<RfbProtocolException>().WithMessage("*8 bytes instead of 16*");
    }

    // ── ZlibHex ────────────────────────────────────────────────────────────

    /// <summary>One zlib stream with a sync flush after each chunk, as UltraVNC writes each compressed tile.</summary>
    private sealed class TileCompressor
    {
        private readonly MemoryStream _output = new();
        private readonly ZLibStream _zlib;
        private long _previous;

        public TileCompressor() => _zlib = new ZLibStream(_output, CompressionLevel.Optimal, leaveOpen: true);

        public byte[] Compress(byte[] payload)
        {
            _zlib.Write(payload);
            _zlib.Flush();
            var compressed = _output.ToArray()[(int)_previous..];
            _previous = _output.Length;
            return compressed;
        }
    }

    [Fact]
    public void ZlibHex_DecodesCompressedAndPlainTiles_WithSeparateStreamsAcrossRectangles()
    {
        // 20×18 → tiles (16×16) (4×16) / (16×2) (4×2).
        var fb = new Framebuffer(20, 18);
        var hex = new TileCompressor();
        var raw = new TileCompressor();

        var tile1 = new RfbBytes().Pixel(Grey).Pixel(Red).U8(1).U8(2 << 4 | 3).U8(3 << 4 | 4);
        var tile2 = new RfbBytes();
        for (uint i = 0; i < 4 * 16; i++) tile2.Pixel(i + 1);
        var tile1Wire = hex.Compress(tile1);
        var tile2Wire = raw.Compress(tile2);

        var data = new RfbBytes()
            // Tile 1: ZlibHex with background, foreground and one sub-rectangle.
            .U8(64 | 2 | 4 | 8).U16(tile1Wire.Length).Raw(tile1Wire)
            // Tile 2: ZlibRaw.
            .U8(32).U16(tile2Wire.Length).Raw(tile2Wire)
            // Tile 3: plain Hextile, one coloured sub-rectangle (which also becomes the foreground).
            .U8(8 | 16).U8(1).Pixel(Blue).U8(0).U8(0)
            // Tile 4: plain Hextile using the carried-over foreground.
            .U8(8).U8(1).U8(1 << 4 | 1).U8(0);

        var decoder = new ZlibHexDecoder();
        Decode(decoder, fb, new RfbRect(0, 0, 20, 18), data);

        fb.GetPixel(0, 0).Should().Be(Colour.Of(Grey));
        fb.GetPixel(2, 3).Should().Be(Colour.Of(Red));
        fb.GetPixel(5, 7).Should().Be(Colour.Of(Red));
        fb.GetPixel(6, 7).Should().Be(Colour.Of(Grey));
        fb.GetPixel(16, 0).Should().Be(Colour.Of(1));
        fb.GetPixel(19, 15).Should().Be(Colour.Of(64));
        fb.GetPixel(0, 16).Should().Be(Colour.Of(Blue));
        fb.GetPixel(1, 16).Should().Be(Colour.Of(Grey), "the background carries over");
        fb.GetPixel(17, 17).Should().Be(Colour.Of(Blue), "a coloured sub-rectangle becomes the foreground");
        fb.GetPixel(16, 16).Should().Be(Colour.Of(Grey));

        // A second rectangle continues both zlib streams.
        var next = hex.Compress(new RfbBytes().Pixel(Green));
        var nextRaw = raw.Compress(new RfbBytes().Pixel(Red).Pixel(Red).Pixel(Red).Pixel(Red));
        Decode(decoder, fb, new RfbRect(0, 0, 4, 2), new RfbBytes().U8(64 | 2).U16(next.Length).Raw(next));
        Decode(decoder, fb, new RfbRect(4, 0, 2, 2), new RfbBytes().U8(32).U16(nextRaw.Length).Raw(nextRaw));

        fb.GetPixel(3, 1).Should().Be(Colour.Of(Green));
        fb.GetPixel(5, 1).Should().Be(Colour.Of(Red));
    }

    [Fact]
    public void ZlibHex_TruncatedCompressedTile_Throws()
    {
        var wire = new TileCompressor().Compress(new RfbBytes().Pixel(Grey));
        var data = new RfbBytes().U8(64 | 2 | 4).U16(wire.Length).Raw(wire).ToArray();

        var act = () => new ZlibHexDecoder().Decode(new RfbReader(new MemoryStream(data)), new Framebuffer(4, 4), new RfbRect(0, 0, 4, 4));

        act.Should().Throw<RfbProtocolException>();
    }

    private static class LibLzo
    {
        private static readonly IntPtr Handle =
            NativeLibrary.TryLoad("liblzo2.so.2", out var handle) || NativeLibrary.TryLoad("liblzo2", out handle) ? handle : IntPtr.Zero;

        public static bool IsAvailable => Handle != IntPtr.Zero;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Lzo1xCompress(byte[] source, nuint sourceLength, byte[] destination, ref nuint destinationLength, byte[] work);

        public static byte[] Compress(byte[] input)
        {
            var compress = Marshal.GetDelegateForFunctionPointer<Lzo1xCompress>(NativeLibrary.GetExport(Handle, "lzo1x_1_compress"));
            var output = new byte[input.Length + input.Length / 16 + 64 + 3];
            nuint length = 0;
            compress(input, (nuint)input.Length, output, ref length, new byte[16384 * IntPtr.Size])
                .Should().Be(0, "lzo1x_1_compress must succeed");
            return output[..(int)length];
        }
    }
}
