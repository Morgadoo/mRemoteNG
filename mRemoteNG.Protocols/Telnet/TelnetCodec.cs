using System.Text;

namespace mRemoteNG.Protocols.Telnet;

/// <summary>A telnet option negotiation command received from the server (RFC 854).</summary>
public readonly record struct TelnetNegotiation(byte Command, byte Option);

/// <summary>
/// Stateful telnet stream codec, independent of sockets and UI.
/// Splits server data into text and option negotiations — correctly across reads that cut an IAC
/// sequence or a UTF-8 character in half — and encodes client input for the wire.
/// </summary>
public sealed class TelnetCodec
{
    public const byte Iac = 255;
    public const byte Dont = 254;
    public const byte Do = 253;
    public const byte Wont = 252;
    public const byte Will = 251;
    public const byte Sb = 250;
    public const byte Se = 240;
    public const byte OptEcho = 1;
    public const byte OptSuppressGoAhead = 3;
    public const byte OptNaws = 31;

    private enum State { Data, Iac, Negotiation, Subnegotiation, SubnegotiationIac }

    private readonly Decoder _utf8 = new UTF8Encoding(false).GetDecoder();
    private State _state = State.Data;
    private byte _pendingCommand;

    /// <summary>
    /// Decodes a chunk of server data. Text is returned; negotiations are appended to
    /// <paramref name="negotiations"/> in the order received.
    /// </summary>
    public string Decode(ReadOnlySpan<byte> data, List<TelnetNegotiation> negotiations)
    {
        var text = new StringBuilder();
        var dataBytes = new List<byte>(data.Length);

        foreach (var b in data)
        {
            switch (_state)
            {
                case State.Data:
                    if (b == Iac)
                        _state = State.Iac;
                    else
                        dataBytes.Add(b);
                    break;

                case State.Iac:
                    if (b == Iac)
                    {
                        dataBytes.Add(Iac); // escaped 0xFF data byte
                        _state = State.Data;
                    }
                    else if (b is Will or Wont or Do or Dont)
                    {
                        _pendingCommand = b;
                        _state = State.Negotiation;
                    }
                    else
                    {
                        // SB starts a subnegotiation; other commands (NOP, GA, …) carry no payload.
                        _state = b == Sb ? State.Subnegotiation : State.Data;
                    }
                    break;

                case State.Negotiation:
                    FlushText(dataBytes, text);
                    negotiations.Add(new TelnetNegotiation(_pendingCommand, b));
                    _state = State.Data;
                    break;

                case State.Subnegotiation:
                    if (b == Iac) _state = State.SubnegotiationIac;
                    break;

                case State.SubnegotiationIac:
                    _state = b == Se ? State.Data : State.Subnegotiation;
                    break;
            }
        }

        FlushText(dataBytes, text);
        return text.ToString();
    }

    private void FlushText(List<byte> bytes, StringBuilder text)
    {
        if (bytes.Count == 0) return;
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes);
        var chars = new char[_utf8.GetCharCount(span, flush: false)];
        _utf8.GetChars(span, chars, flush: false);
        text.Append(chars);
        bytes.Clear();
    }

    /// <summary>
    /// Encodes terminal input for the wire: doubles 0xFF (IAC) and sends a bare CR as CR NUL,
    /// as RFC 854 requires (Enter in the terminal produces a lone CR).
    /// </summary>
    public static byte[] EncodeInput(ReadOnlySpan<byte> input)
    {
        var output = new List<byte>(input.Length + 4);
        for (var i = 0; i < input.Length; i++)
        {
            var b = input[i];
            output.Add(b);
            if (b == Iac)
                output.Add(Iac);
            else if (b == (byte)'\r' && (i + 1 >= input.Length || input[i + 1] != (byte)'\n'))
                output.Add(0);
        }
        return output.ToArray();
    }

    /// <summary>Builds a NAWS (RFC 1073) window-size subnegotiation; 0xFF size bytes are doubled.</summary>
    public static byte[] EncodeWindowSize(int columns, int rows)
    {
        var output = new List<byte> { Iac, Sb, OptNaws };
        foreach (var value in new[] { columns, rows })
        {
            var clamped = (ushort)Math.Clamp(value, 0, ushort.MaxValue);
            foreach (var b in new[] { (byte)(clamped >> 8), (byte)(clamped & 0xFF) })
            {
                output.Add(b);
                if (b == Iac) output.Add(Iac);
            }
        }
        output.Add(Iac);
        output.Add(Se);
        return output.ToArray();
    }

    // Option state (RFC 1143, simplified): only state changes are acknowledged, so a server
    // repeating a negotiation can't start a WILL/DO loop.
    private readonly HashSet<byte> _remoteEnabled = [];
    private readonly HashSet<byte> _remoteRefused = [];
    private readonly HashSet<byte> _localEnabled = [];
    private readonly HashSet<byte> _localRefused = [];
    private bool _windowSizeOffered;

    /// <summary>True once the server agreed to receive window-size updates (NAWS).</summary>
    public bool WindowSizeEnabled => _localEnabled.Contains(OptNaws);

    /// <summary>Our initial offer to send the window size.</summary>
    public byte[] OfferWindowSize()
    {
        _windowSizeOffered = true;
        return [Iac, Will, OptNaws];
    }

    /// <summary>
    /// Our response to a server negotiation (possibly empty). We let the server echo and suppress
    /// go-ahead, send our window size, and refuse every other option.
    /// </summary>
    public byte[] Respond(TelnetNegotiation negotiation, int columns, int rows)
    {
        var option = negotiation.Option;
        switch (negotiation.Command)
        {
            case Will when option is OptEcho or OptSuppressGoAhead:
                return _remoteEnabled.Add(option) ? [Iac, Do, option] : [];
            case Will:
                return _remoteRefused.Add(option) ? [Iac, Dont, option] : [];
            case Wont:
                _remoteRefused.Add(option);
                return _remoteEnabled.Remove(option) ? [Iac, Dont, option] : [];
            case Do when option == OptNaws:
            {
                var reply = new List<byte>();
                if (_localEnabled.Add(OptNaws) && !_windowSizeOffered)
                    reply.AddRange([Iac, Will, OptNaws]);
                reply.AddRange(EncodeWindowSize(columns, rows));
                return reply.ToArray();
            }
            case Do:
                return _localRefused.Add(option) ? [Iac, Wont, option] : [];
            case Dont:
                _localRefused.Add(option);
                return _localEnabled.Remove(option) ? [Iac, Wont, option] : [];
            default:
                return [];
        }
    }
}
