namespace mRemoteNG.Protocols.Vnc.Rfb.Decoders;

/// <summary>
/// Encoding 6 (Zlib): Raw pixels compressed with one zlib stream that spans the connection;
/// each rectangle is a u32 length followed by that many compressed bytes.
/// </summary>
internal sealed class ZlibDecoder(PixelConverter? converter = null) : IRectangleDecoder
{
    private readonly PixelConverter _converter = converter ?? PixelConverter.Bgra32;
    private readonly ZlibInflater _inflater = new();
    private byte[] _row = [];
    private uint[] _pixels = [];

    public void Decode(RfbReader reader, Framebuffer framebuffer, RfbRect rect)
    {
        var length = reader.ReadUInt32();
        _inflater.Append(reader, (int)Math.Min(length, int.MaxValue));

        var rowBytes = rect.Width * _converter.BytesPerPixel;
        if (_row.Length < rowBytes) _row = new byte[rowBytes];
        if (_pixels.Length < rect.Width) _pixels = new uint[rect.Width];
        var row = _row.AsSpan(0, rowBytes);
        var pixels = _pixels.AsSpan(0, rect.Width);

        for (var y = 0; y < rect.Height; y++)
        {
            _inflater.ReadExactly(row);
            _converter.ConvertRow(row, pixels);
            framebuffer.Write(rect.X, rect.Y + y, rect.Width, 1, pixels);
        }
    }
}
