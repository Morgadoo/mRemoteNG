using System.Runtime.InteropServices;
using SkiaSharp;

namespace mRemoteNG.Protocols.Vnc.Rfb.Decoders;

/// <summary>
/// Encoding 7 (Tight). Each rectangle starts with a compression-control byte:
/// <list type="bullet">
/// <item>bits 0–3 reset the corresponding one of four persistent zlib streams;</item>
/// <item>high nibble 8 = fill (one TPIXEL), 9 = JPEG, 0–7 = basic compression where bits 4–5 pick the zlib
/// stream and bit 6 says an explicit filter id (copy, palette or gradient) follows.</item>
/// </list>
/// Basic data shorter than 12 bytes is sent uncompressed; longer data is a compact length plus zlib bytes.
/// </summary>
internal sealed class TightDecoder(PixelConverter? converter = null) : IRectangleDecoder
{
    private const int FillCompression = 0x08;
    private const int JpegCompression = 0x09;
    private const int PngCompression = 0x0A;
    private const int FilterCopy = 0;
    private const int FilterPalette = 1;
    private const int FilterGradient = 2;
    private const int MinCompressedSize = 12;
    private const int MaxDataLength = 64 * 1024 * 1024;

    private readonly PixelConverter _converter = converter ?? PixelConverter.Bgra32;
    private readonly TightStatistics _statistics = new();
    private readonly ZlibInflater[] _streams = [new(), new(), new(), new()];
    private readonly uint[] _palette = new uint[256];
    private byte[] _data = new byte[64 * 1024];
    private uint[] _pixels = new uint[16 * 1024];

    /// <summary>How many rectangles used each Tight method (diagnostics; updated on the receive thread).</summary>
    public TightStatistics Statistics => _statistics;

    public void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect)
    {
        int control = reader.ReadByte();
        for (var i = 0; i < 4; i++)
        {
            if ((control & (1 << i)) != 0)
                _streams[i].Reset();
        }

        var type = control >> 4;
        switch (type)
        {
            case FillCompression:
            {
                Statistics.Fill++;
                Span<byte> pixel = stackalloc byte[4];
                pixel = pixel[.._converter.TightBytesPerPixel];
                reader.ReadExactly(pixel);
                framebuffer.Fill(rect.X, rect.Y, rect.Width, rect.Height, _converter.ReadTightPixel(pixel));
                return;
            }

            case JpegCompression:
                Statistics.Jpeg++;
                DecodeJpeg(reader, framebuffer, rect);
                return;

            case PngCompression:
                throw new RfbProtocolException("The server sent TightPNG data, which was not requested.");

            case > 7:
                throw new RfbProtocolException($"Invalid Tight compression control 0x{control:X2}.");
        }

        var stream = type & 0x03;
        var filter = (type & 0x04) != 0 ? reader.ReadByte() : FilterCopy;
        var pixelCount = rect.Width * rect.Height;
        var pixels = Pixels(pixelCount);
        var tpixel = _converter.TightBytesPerPixel;

        switch (filter)
        {
            case FilterCopy:
            {
                Statistics.Copy++;
                var data = ReadData(reader, stream, pixelCount * tpixel);
                for (var i = 0; i < pixelCount; i++)
                    pixels[i] = _converter.ReadTightPixel(data.Slice(i * tpixel, tpixel));
                break;
            }

            case FilterPalette:
            {
                Statistics.Palette++;
                var colours = reader.ReadByte() + 1;
                Span<byte> pixel = stackalloc byte[4];
                pixel = pixel[..tpixel];
                for (var i = 0; i < colours; i++)
                {
                    reader.ReadExactly(pixel);
                    _palette[i] = _converter.ReadTightPixel(pixel);
                }

                if (colours == 2)
                {
                    // One bit per pixel, most significant bit first, each row padded to a whole byte.
                    var stride = (rect.Width + 7) / 8;
                    var data = ReadData(reader, stream, stride * rect.Height);
                    for (var y = 0; y < rect.Height; y++)
                    {
                        for (var x = 0; x < rect.Width; x++)
                        {
                            var bit = (data[y * stride + x / 8] >> (7 - x % 8)) & 1;
                            pixels[y * rect.Width + x] = _palette[bit];
                        }
                    }
                }
                else
                {
                    var data = ReadData(reader, stream, pixelCount);
                    for (var i = 0; i < pixelCount; i++)
                    {
                        if (data[i] >= colours)
                            throw new RfbProtocolException($"Tight palette index {data[i]} out of range.");
                        pixels[i] = _palette[data[i]];
                    }
                }
                break;
            }

            case FilterGradient:
            {
                Statistics.Gradient++;
                var data = ReadData(reader, stream, pixelCount * tpixel);
                if (_converter.TightPixelIsRgb24)
                    ApplyGradient24(data, pixels, rect.Width, rect.Height);
                else
                    ApplyGradient(data, pixels, rect.Width, rect.Height);
                break;
            }

            default:
                throw new RfbProtocolException($"Unknown Tight filter {filter}.");
        }

        framebuffer.Write(rect.X, rect.Y, rect.Width, rect.Height, pixels);
    }

    /// <summary>Reads the (possibly compressed) payload of a basic-compression rectangle.</summary>
    private ReadOnlySpan<byte> ReadData(RfbReader reader, int stream, int length)
    {
        if (length > MaxDataLength)
            throw new RfbProtocolException($"Tight rectangle of {length} bytes is implausibly large.");
        if (_data.Length < length)
            _data = new byte[Math.Max(length, _data.Length * 2)];
        var data = _data.AsSpan(0, length);

        if (length < MinCompressedSize)
        {
            reader.ReadExactly(data);
        }
        else
        {
            var inflater = _streams[stream];
            inflater.Append(reader, ReadCompactLength(reader));
            inflater.ReadExactly(data);
        }
        return data;
    }

    private void DecodeJpeg(RfbReader reader, Framebuffer framebuffer, RfbRect rect)
    {
        var length = ReadCompactLength(reader);
        var jpeg = reader.ReadBytes(length);

        using var codec = SKCodec.Create(new SKMemoryStream(jpeg))
            ?? throw new RfbProtocolException("The server sent a Tight JPEG rectangle that could not be decoded.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        if (info.Width != rect.Width || info.Height != rect.Height)
            throw new RfbProtocolException($"Tight JPEG image is {info.Width}×{info.Height}, expected {rect.Width}×{rect.Height}.");

        var pixels = Pixels(info.Width * info.Height);
        // _pixels is at least as large as the image; the codec writes rows of width × 4 bytes into it.
        var handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
        try
        {
            var result = codec.GetPixels(info, handle.AddrOfPinnedObject());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new RfbProtocolException($"Tight JPEG decoding failed: {result}.");
        }
        finally
        {
            handle.Free();
        }
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] |= 0xFF000000u;
        framebuffer.Write(rect.X, rect.Y, rect.Width, rect.Height, pixels);
    }

    /// <summary>Gradient filter for 24-bit TPIXELs: each R, G, B byte is predicted from its left, upper and upper-left neighbours.</summary>
    private static void ApplyGradient24(ReadOnlySpan<byte> data, Span<uint> pixels, int width, int height)
    {
        var previous = new int[width * 3];
        var current = new int[width * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var left = x > 0 ? current[(x - 1) * 3 + c] : 0;
                    var upperLeft = x > 0 ? previous[(x - 1) * 3 + c] : 0;
                    var predicted = Math.Clamp(previous[x * 3 + c] + left - upperLeft, 0, 255);
                    current[x * 3 + c] = (predicted + data[(y * width + x) * 3 + c]) & 0xFF;
                }
                pixels[y * width + x] = PixelConverter.FromRgb(
                    (byte)current[x * 3], (byte)current[x * 3 + 1], (byte)current[x * 3 + 2]);
            }
            (previous, current) = (current, previous);
        }
    }

    /// <summary>Gradient filter for full pixels: prediction works on the format's red, green and blue components.</summary>
    private void ApplyGradient(ReadOnlySpan<byte> data, Span<uint> pixels, int width, int height)
    {
        var format = _converter.Format;
        int[] max = [format.RedMax, format.GreenMax, format.BlueMax];
        int[] shift = [format.RedShift, format.GreenShift, format.BlueShift];
        var bpp = _converter.BytesPerPixel;
        var previous = new int[width * 3];
        var current = new int[width * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = _converter.ReadValue(data.Slice((y * width + x) * bpp, bpp));
                uint result = 0;
                for (var c = 0; c < 3; c++)
                {
                    var left = x > 0 ? current[(x - 1) * 3 + c] : 0;
                    var upperLeft = x > 0 ? previous[(x - 1) * 3 + c] : 0;
                    var predicted = Math.Clamp(previous[x * 3 + c] + left - upperLeft, 0, max[c]);
                    var component = (predicted + (int)(value >> shift[c])) & max[c];
                    current[x * 3 + c] = component;
                    result |= (uint)component << shift[c];
                }
                pixels[y * width + x] = _converter.ToBgra(result);
            }
            (previous, current) = (current, previous);
        }
    }

    /// <summary>Tight's 1–3 byte little-endian length, 7 bits per byte with a continuation bit.</summary>
    internal static int ReadCompactLength(RfbReader reader)
    {
        int b = reader.ReadByte();
        var length = b & 0x7F;
        if ((b & 0x80) != 0)
        {
            b = reader.ReadByte();
            length |= (b & 0x7F) << 7;
            if ((b & 0x80) != 0)
                length |= reader.ReadByte() << 14;
        }
        return length;
    }

    private Span<uint> Pixels(int count)
    {
        if (_pixels.Length < count)
            _pixels = new uint[Math.Max(count, _pixels.Length * 2)];
        return _pixels.AsSpan(0, count);
    }
}

/// <summary>Counts of Tight rectangles by compression method and filter.</summary>
internal sealed class TightStatistics
{
    public long Fill;
    public long Jpeg;
    public long Copy;
    public long Palette;
    public long Gradient;

    public override string ToString() =>
        $"fill {Fill}, jpeg {Jpeg}, copy {Copy}, palette {Palette}, gradient {Gradient}";
}
