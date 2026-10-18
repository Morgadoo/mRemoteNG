namespace mRemoteNG.Protocols.Vnc.Rfb.Decoders;

/// <summary>
/// LZO1X decompressor (the format of <c>lzo1x_1_compress</c>, as used by UltraVNC's Ultra encoding), ported from
/// <c>lzo1x_decompress_safe</c>: every read and write is bounds-checked, so corrupt input fails instead of
/// overrunning a buffer.
/// </summary>
internal static class Lzo1x
{
    private const int M2MaxOffset = 0x0800;

    private enum State
    {
        LiteralRun,
        FirstLiteralRun,
        Match,
        MatchDone,
        MatchNext,
    }

    /// <summary>Decompresses <paramref name="input"/> into <paramref name="output"/>; returns the bytes written.</summary>
    /// <exception cref="RfbProtocolException">The data is corrupt, truncated or does not fit in the output.</exception>
    public static int Decompress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        try
        {
            return DecompressCore(input, output);
        }
        catch (IndexOutOfRangeException)
        {
            throw new RfbProtocolException("LZO data is corrupt or larger than the rectangle.");
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new RfbProtocolException("LZO data is corrupt or larger than the rectangle.");
        }
    }

    private static int DecompressCore(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int ip = 0, op = 0, t;
        State state;

        if (input[0] > 17)
        {
            t = input[ip++] - 17;
            if (t < 4)
            {
                state = State.MatchNext;
            }
            else
            {
                CopyLiterals(input, ref ip, output, ref op, t);
                state = State.FirstLiteralRun;
            }
        }
        else
        {
            t = 0;
            state = State.LiteralRun;
        }

        while (true)
        {
            switch (state)
            {
                case State.LiteralRun:
                    t = input[ip++];
                    if (t >= 16)
                    {
                        state = State.Match;
                        break;
                    }
                    if (t == 0)
                        t = 15 + ReadRunLength(input, ref ip, output.Length);
                    CopyLiterals(input, ref ip, output, ref op, t + 3);
                    state = State.FirstLiteralRun;
                    break;

                case State.FirstLiteralRun:
                    t = input[ip++];
                    if (t >= 16)
                    {
                        state = State.Match;
                        break;
                    }
                    CopyMatch(output, ref op, op - (1 + M2MaxOffset) - (t >> 2) - (input[ip++] << 2), 3);
                    state = State.MatchDone;
                    break;

                case State.Match:
                    if (t >= 64)
                    {
                        // M2: 3-8 bytes within 2 KiB.
                        var distance = 1 + ((t >> 2) & 7) + (input[ip++] << 3);
                        CopyMatch(output, ref op, op - distance, (t >> 5) - 1 + 2);
                    }
                    else if (t >= 32)
                    {
                        // M3: any length within 16 KiB.
                        t &= 31;
                        if (t == 0)
                            t = 31 + ReadRunLength(input, ref ip, output.Length);
                        var distance = 1 + (input[ip] >> 2) + (input[ip + 1] << 6);
                        ip += 2;
                        CopyMatch(output, ref op, op - distance, t + 2);
                    }
                    else if (t >= 16)
                    {
                        // M4: any length within 48 KiB; distance 0 marks the end of the stream.
                        var distance = (t & 8) << 11;
                        t &= 7;
                        if (t == 0)
                            t = 7 + ReadRunLength(input, ref ip, output.Length);
                        distance += (input[ip] >> 2) + (input[ip + 1] << 6);
                        ip += 2;
                        if (distance == 0)
                        {
                            if (ip != input.Length)
                                throw new RfbProtocolException("LZO data continues after the end-of-stream marker.");
                            return op;
                        }
                        CopyMatch(output, ref op, op - distance - 0x4000, t + 2);
                    }
                    else
                    {
                        // M1: 2 bytes within 1 KiB.
                        CopyMatch(output, ref op, op - 1 - (t >> 2) - (input[ip++] << 2), 2);
                    }
                    state = State.MatchDone;
                    break;

                case State.MatchDone:
                    t = input[ip - 2] & 3;
                    state = t == 0 ? State.LiteralRun : State.MatchNext;
                    break;

                case State.MatchNext:
                    CopyLiterals(input, ref ip, output, ref op, t);
                    t = input[ip++];
                    state = State.Match;
                    break;
            }
        }
    }

    /// <summary>Reads the zero-extended part of a run length (each 0 byte adds 255, then a final non-zero byte).</summary>
    private static int ReadRunLength(ReadOnlySpan<byte> input, ref int ip, int limit)
    {
        var length = 0;
        while (input[ip] == 0)
        {
            length += 255;
            ip++;
            if (length > limit)
                throw new RfbProtocolException("LZO run length exceeds the rectangle.");
        }
        return length + input[ip++];
    }

    private static void CopyLiterals(ReadOnlySpan<byte> input, ref int ip, Span<byte> output, ref int op, int count)
    {
        input.Slice(ip, count).CopyTo(output.Slice(op, count));
        ip += count;
        op += count;
    }

    /// <summary>Copies <paramref name="count"/> bytes from earlier output; the ranges may overlap (runs).</summary>
    private static void CopyMatch(Span<byte> output, ref int op, int from, int count)
    {
        if (from < 0)
            throw new RfbProtocolException("LZO match refers to data before the start of the rectangle.");
        if (op - from >= count)
        {
            output.Slice(from, count).CopyTo(output.Slice(op, count));
            op += count;
            return;
        }
        var destination = output.Slice(op, count);
        for (var i = 0; i < count; i++)
            destination[i] = output[from + i];
        op += count;
    }
}
