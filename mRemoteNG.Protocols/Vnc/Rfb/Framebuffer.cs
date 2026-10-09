namespace mRemoteNG.Protocols.Vnc.Rfb;

/// <summary>Integer rectangle in framebuffer pixels.</summary>
public readonly record struct RfbRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public RfbRect Union(RfbRect other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new RfbRect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }
}

/// <summary>
/// Client-side copy of the remote desktop: opaque BGRA pixels (one <see cref="uint"/> per pixel, row-major,
/// stride = width) plus a coalesced dirty rectangle.
/// <para>
/// Pixels are written only by the receive loop. A renderer may read them concurrently without locking; any
/// region it reads mid-update is marked dirty again once the update completes, so the display converges.
/// Resizing swaps in a new array, so readers must capture <see cref="Snapshot"/> once per pass.
/// </para>
/// </summary>
public sealed class Framebuffer
{
    private readonly object _dirtyLock = new();
    private RfbRect _dirty;
    private volatile PixelBuffer _buffer;

    public Framebuffer(int width, int height)
    {
        _buffer = new PixelBuffer(width, height);
        _dirty = new RfbRect(0, 0, width, height);
    }

    public int Width => _buffer.Width;
    public int Height => _buffer.Height;

    /// <summary>Pixels of the current size; replaced on resize.</summary>
    public uint[] Pixels => _buffer.Pixels;

    /// <summary>The current pixel array together with its dimensions.</summary>
    public PixelBuffer Snapshot => _buffer;

    /// <summary>
    /// Reallocates the framebuffer (contents become black) and marks it entirely dirty.
    /// Returns false, changing nothing, when the size is unchanged.
    /// </summary>
    public bool Resize(int width, int height)
    {
        if (width == Width && height == Height) return false;
        var buffer = new PixelBuffer(width, height);
        lock (_dirtyLock)
        {
            _buffer = buffer;
            _dirty = new RfbRect(0, 0, width, height);
        }
        return true;
    }

    public void MarkDirty(RfbRect rect)
    {
        rect = Clip(rect);
        if (rect.IsEmpty) return;
        lock (_dirtyLock) _dirty = _dirty.Union(rect);
    }

    /// <summary>Returns and clears the accumulated dirty rectangle (empty when nothing changed).</summary>
    public RfbRect TakeDirty()
    {
        lock (_dirtyLock)
        {
            var dirty = _dirty;
            _dirty = default;
            return dirty;
        }
    }

    public uint GetPixel(int x, int y) => Pixels[y * Width + x];

    /// <summary>Fills a rectangle, clipped to the framebuffer.</summary>
    public void Fill(int x, int y, int width, int height, uint colour)
    {
        var r = Clip(new RfbRect(x, y, width, height));
        if (r.IsEmpty) return;
        var pixels = Pixels;
        var stride = Width;
        for (var row = r.Y; row < r.Bottom; row++)
            pixels.AsSpan(row * stride + r.X, r.Width).Fill(colour);
    }

    /// <summary>Copies a block of <paramref name="source"/> pixels (stride = <paramref name="width"/>) into place.</summary>
    public void Write(int x, int y, int width, int height, ReadOnlySpan<uint> source)
    {
        var r = Clip(new RfbRect(x, y, width, height));
        if (r.IsEmpty) return;
        var pixels = Pixels;
        var stride = Width;
        for (var row = r.Y; row < r.Bottom; row++)
        {
            source.Slice((row - y) * width + (r.X - x), r.Width)
                .CopyTo(pixels.AsSpan(row * stride + r.X, r.Width));
        }
    }

    /// <summary>Copies a rectangle within the framebuffer; overlapping source and destination are handled.</summary>
    public void Copy(int srcX, int srcY, int dstX, int dstY, int width, int height)
    {
        var dst = Clip(new RfbRect(dstX, dstY, width, height));
        if (dst.IsEmpty) return;
        // Shift the source by however much the destination was clipped, then clip the source too.
        srcX += dst.X - dstX;
        srcY += dst.Y - dstY;
        var src = Clip(new RfbRect(srcX, srcY, dst.Width, dst.Height));
        if (src.IsEmpty) return;
        dst = new RfbRect(dst.X + (src.X - srcX), dst.Y + (src.Y - srcY), src.Width, src.Height);

        var pixels = Pixels;
        var stride = Width;
        if (src.Y < dst.Y)
        {
            // Moving down: copy bottom-up so rows are read before they are overwritten.
            for (var row = src.Height - 1; row >= 0; row--)
                pixels.AsSpan((src.Y + row) * stride + src.X, src.Width)
                    .CopyTo(pixels.AsSpan((dst.Y + row) * stride + dst.X, src.Width));
        }
        else
        {
            // Span.CopyTo is memmove, so horizontal overlap within a row is safe.
            for (var row = 0; row < src.Height; row++)
                pixels.AsSpan((src.Y + row) * stride + src.X, src.Width)
                    .CopyTo(pixels.AsSpan((dst.Y + row) * stride + dst.X, src.Width));
        }
    }

    private RfbRect Clip(RfbRect r)
    {
        var x = Math.Max(r.X, 0);
        var y = Math.Max(r.Y, 0);
        var right = Math.Min(r.Right, Width);
        var bottom = Math.Min(r.Bottom, Height);
        return right <= x || bottom <= y ? default : new RfbRect(x, y, right - x, bottom - y);
    }
}

/// <summary>An immutable pairing of a pixel array with its dimensions.</summary>
public sealed class PixelBuffer
{
    public PixelBuffer(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new uint[Math.Max(width, 0) * Math.Max(height, 0)];
    }

    public int Width { get; }
    public int Height { get; }
    public uint[] Pixels { get; }
}
