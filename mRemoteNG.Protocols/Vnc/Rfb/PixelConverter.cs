using System.Runtime.InteropServices;

namespace mRemoteNG.Protocols.Vnc.Rfb;

/// <summary>
/// Converts pixels in the negotiated client <see cref="PixelFormat"/> into the opaque BGRA values of the
/// <see cref="Framebuffer"/>. Besides plain pixels it reads the two compact forms some encodings use:
/// ZRLE's CPIXEL (padding byte dropped) and Tight's TPIXEL (R, G, B bytes for 24-bit colour).
/// </summary>
public sealed class PixelConverter
{
    private readonly uint[]? _table;
    private readonly bool _isBgra32;
    private readonly int _compactShift;

    public PixelConverter(PixelFormat format)
    {
        if (!format.TrueColour)
            throw new ArgumentException("Only true-colour pixel formats are supported.", nameof(format));
        if (format.BitsPerPixel is not (8 or 16 or 32))
            throw new ArgumentException($"Unsupported bits per pixel: {format.BitsPerPixel}.", nameof(format));

        Format = format;
        BytesPerPixel = format.BytesPerPixel;
        _isBgra32 = format == PixelFormat.Bgra32;

        // Lookup tables make 8 and 16 bpp conversion a single array access.
        if (format.BitsPerPixel <= 16)
        {
            _table = new uint[1 << format.BitsPerPixel];
            for (var v = 0; v < _table.Length; v++)
                _table[v] = Convert((uint)v);
        }

        // ZRLE CPIXEL: 3 bytes when 32bpp, depth <= 24 and the colour bits fit in the low or high 3 bytes.
        var colourBits = (uint)format.RedMax << format.RedShift
            | (uint)format.GreenMax << format.GreenShift
            | (uint)format.BlueMax << format.BlueShift;
        if (format.BitsPerPixel == 32 && format.Depth <= 24 && (colourBits & 0xFF000000u) == 0)
        {
            CompactBytesPerPixel = 3;
            _compactShift = 0;
        }
        else if (format.BitsPerPixel == 32 && format.Depth <= 24 && (colourBits & 0xFFu) == 0)
        {
            CompactBytesPerPixel = 3;
            _compactShift = 8;
        }
        else
        {
            CompactBytesPerPixel = BytesPerPixel;
        }

        // Tight TPIXEL: R, G, B bytes for 32bpp depth-24 formats with 8-bit components.
        TightPixelIsRgb24 = format.BitsPerPixel == 32 && format.Depth == 24
            && format.RedMax == 255 && format.GreenMax == 255 && format.BlueMax == 255;
    }

    public static PixelConverter Bgra32 { get; } = new(PixelFormat.Bgra32);

    public PixelFormat Format { get; }

    public int BytesPerPixel { get; }

    /// <summary>Size of a ZRLE CPIXEL.</summary>
    public int CompactBytesPerPixel { get; }

    /// <summary>True when Tight sends pixels as three bytes R, G, B.</summary>
    public bool TightPixelIsRgb24 { get; }

    /// <summary>Size of a Tight TPIXEL.</summary>
    public int TightBytesPerPixel => TightPixelIsRgb24 ? 3 : BytesPerPixel;

    /// <summary>Reads the raw pixel value (in the format's byte order) from <paramref name="b"/>.</summary>
    public uint ReadValue(ReadOnlySpan<byte> b) => BytesPerPixel switch
    {
        1 => b[0],
        2 => Format.BigEndian ? (uint)(b[0] << 8 | b[1]) : (uint)(b[1] << 8 | b[0]),
        _ => Format.BigEndian
            ? (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3]
            : (uint)b[3] << 24 | (uint)b[2] << 16 | (uint)b[1] << 8 | b[0],
    };

    /// <summary>Converts a raw pixel value to opaque BGRA.</summary>
    public uint ToBgra(uint value)
    {
        if (_table is not null) return _table[value & (uint)(_table.Length - 1)];
        if (_isBgra32) return value | 0xFF000000u;
        return Convert(value);
    }

    /// <summary>Reads one pixel (<see cref="BytesPerPixel"/> bytes) as opaque BGRA.</summary>
    public uint ReadPixel(ReadOnlySpan<byte> b) => ToBgra(ReadValue(b));

    /// <summary>Reads one ZRLE CPIXEL (<see cref="CompactBytesPerPixel"/> bytes) as opaque BGRA.</summary>
    public uint ReadCompactPixel(ReadOnlySpan<byte> b)
    {
        if (CompactBytesPerPixel != 3) return ReadPixel(b);
        var value = Format.BigEndian
            ? (uint)b[0] << 16 | (uint)b[1] << 8 | b[2]
            : (uint)b[2] << 16 | (uint)b[1] << 8 | b[0];
        return ToBgra(value << _compactShift);
    }

    /// <summary>Reads one Tight TPIXEL (<see cref="TightBytesPerPixel"/> bytes) as opaque BGRA.</summary>
    public uint ReadTightPixel(ReadOnlySpan<byte> b) =>
        TightPixelIsRgb24 ? FromRgb(b[0], b[1], b[2]) : ReadPixel(b);

    public static uint FromRgb(byte r, byte g, byte b) => 0xFF000000u | (uint)r << 16 | (uint)g << 8 | b;

    /// <summary>Converts a row of wire pixels to BGRA; <paramref name="destination"/> holds one value per pixel.</summary>
    public void ConvertRow(ReadOnlySpan<byte> source, Span<uint> destination)
    {
        if (_isBgra32)
        {
            // x64 and ARM64 are little-endian, so the wire bytes B, G, R, X are already the BGRA value.
            var pixels = MemoryMarshal.Cast<byte, uint>(source);
            for (var i = 0; i < destination.Length; i++)
                destination[i] = pixels[i] | 0xFF000000u;
            return;
        }

        var bpp = BytesPerPixel;
        for (var i = 0; i < destination.Length; i++)
            destination[i] = ReadPixel(source.Slice(i * bpp, bpp));
    }

    /// <summary>Scales each component to 8 bits.</summary>
    private uint Convert(uint value)
    {
        var f = Format;
        var r = Scale((value >> f.RedShift) & f.RedMax, f.RedMax);
        var g = Scale((value >> f.GreenShift) & f.GreenMax, f.GreenMax);
        var b = Scale((value >> f.BlueShift) & f.BlueMax, f.BlueMax);
        return 0xFF000000u | r << 16 | g << 8 | b;
    }

    private static uint Scale(uint component, uint max) =>
        max switch
        {
            0 => 0,
            255 => component,
            _ => (component * 255 + max / 2) / max,
        };
}
