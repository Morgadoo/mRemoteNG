using System.IO.Compression;

namespace mRemoteNG.Protocols.Vnc.Rfb.Decoders;

/// <summary>
/// A zlib stream that spans many rectangles, as used by the Zlib, ZRLE and Tight encodings: the compressed bytes
/// of each rectangle are appended as they arrive, and the server ends each chunk with a sync flush, so everything
/// for the rectangle can be inflated before the next one is read.
/// </summary>
internal sealed class ZlibInflater : IDisposable
{
    private const int MaxCompressedLength = 64 * 1024 * 1024;

    private readonly ContinuousInputStream _input = new();
    private ZLibStream _inflater;

    public ZlibInflater() => _inflater = new ZLibStream(_input, CompressionMode.Decompress);

    /// <summary>Reads <paramref name="count"/> compressed bytes from the connection into the stream's input.</summary>
    public void Append(RfbReader reader, int count)
    {
        if (count is < 0 or > MaxCompressedLength)
            throw new RfbProtocolException($"Compressed block of {count} bytes is implausibly large.");
        _input.Append(reader, count);
    }

    /// <summary>Inflates as much as is available into <paramref name="buffer"/>; 0 means the input is exhausted.</summary>
    public int Read(Span<byte> buffer) => buffer.IsEmpty ? 0 : _inflater.Read(buffer);

    /// <summary>Inflates exactly <paramref name="buffer"/>.Length bytes from the input received so far.</summary>
    public void ReadExactly(Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            var read = _inflater.Read(buffer);
            if (read <= 0)
                throw new RfbProtocolException("Compressed data ended before the rectangle was complete.");
            buffer = buffer[read..];
        }
    }

    /// <summary>Inflates everything available, growing <paramref name="data"/> as needed; returns the length.</summary>
    public int ReadAll(ref byte[] data)
    {
        var length = 0;
        while (true)
        {
            if (length == data.Length)
                Array.Resize(ref data, data.Length * 2);
            var read = _inflater.Read(data, length, data.Length - length);
            if (read <= 0) return length;
            length += read;
        }
    }

    /// <summary>Discards the stream state, as Tight's "reset stream" bits request.</summary>
    public void Reset()
    {
        _inflater.Dispose();
        _input.Clear();
        _inflater = new ZLibStream(_input, CompressionMode.Decompress);
    }

    public void Dispose() => _inflater.Dispose();

    /// <summary>
    /// Read-only stream fed one compressed block at a time. Reading past the fed data returns 0 rather than
    /// blocking, which tells the inflater to stop until the next block arrives.
    /// </summary>
    private sealed class ContinuousInputStream : Stream
    {
        private byte[] _buffer = new byte[64 * 1024];
        private int _start;
        private int _end;

        public void Append(RfbReader reader, int count)
        {
            if (_start == _end) _start = _end = 0;
            if (_buffer.Length - _end < count)
            {
                var pending = _end - _start;
                var target = _buffer.Length >= pending + count ? _buffer : new byte[Math.Max(_buffer.Length * 2, pending + count)];
                Buffer.BlockCopy(_buffer, _start, target, 0, pending);
                _buffer = target;
                _start = 0;
                _end = pending;
            }
            reader.ReadExactly(_buffer.AsSpan(_end, count));
            _end += count;
        }

        public void Clear() => _start = _end = 0;

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = Math.Min(buffer.Length, _end - _start);
            _buffer.AsSpan(_start, n).CopyTo(buffer);
            _start += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
