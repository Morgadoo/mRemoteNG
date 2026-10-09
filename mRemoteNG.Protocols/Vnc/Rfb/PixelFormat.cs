namespace mRemoteNG.Protocols.Vnc.Rfb;

/// <summary>The 16-byte RFB PIXEL_FORMAT structure.</summary>
public readonly record struct PixelFormat(
    byte BitsPerPixel,
    byte Depth,
    bool BigEndian,
    bool TrueColour,
    ushort RedMax,
    ushort GreenMax,
    ushort BlueMax,
    byte RedShift,
    byte GreenShift,
    byte BlueShift)
{
    public const int Size = 16;

    /// <summary>
    /// 32bpp true colour, little-endian, 0x00RRGGBB: on the wire each pixel is the bytes B, G, R, X,
    /// which is exactly the BGRA layout of the client framebuffer.
    /// </summary>
    public static PixelFormat Bgra32 { get; } = new(32, 24, false, true, 255, 255, 255, 16, 8, 0);

    public static PixelFormat Read(RfbReader reader)
    {
        Span<byte> b = stackalloc byte[Size];
        reader.ReadExactly(b);
        return new PixelFormat(
            b[0], b[1], b[2] != 0, b[3] != 0,
            (ushort)(b[4] << 8 | b[5]),
            (ushort)(b[6] << 8 | b[7]),
            (ushort)(b[8] << 8 | b[9]),
            b[10], b[11], b[12]);
    }

    public void Write(Span<byte> b)
    {
        b[0] = BitsPerPixel;
        b[1] = Depth;
        b[2] = (byte)(BigEndian ? 1 : 0);
        b[3] = (byte)(TrueColour ? 1 : 0);
        b[4] = (byte)(RedMax >> 8);
        b[5] = (byte)RedMax;
        b[6] = (byte)(GreenMax >> 8);
        b[7] = (byte)GreenMax;
        b[8] = (byte)(BlueMax >> 8);
        b[9] = (byte)BlueMax;
        b[10] = RedShift;
        b[11] = GreenShift;
        b[12] = BlueShift;
        b[13] = b[14] = b[15] = 0;
    }

    public override string ToString() =>
        $"{BitsPerPixel}bpp depth {Depth} {(TrueColour ? "true colour" : "colour map")} {(BigEndian ? "BE" : "LE")}";
}
