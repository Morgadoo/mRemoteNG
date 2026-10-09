using System.Text;
using FluentAssertions;
using mRemoteNG.Protocols.Telnet;
using Xunit;
using static mRemoteNG.Protocols.Telnet.TelnetCodec;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Telnet;

public class TelnetCodecTests
{
    [Fact]
    public void Decode_SeparatesTextFromNegotiations()
    {
        var codec = new TelnetCodec();
        var negotiations = new List<TelnetNegotiation>();

        var text = codec.Decode([(byte)'h', (byte)'i', Iac, Will, OptEcho, (byte)'!'], negotiations);

        text.Should().Be("hi!");
        negotiations.Should().Equal(new TelnetNegotiation(Will, OptEcho));
    }

    [Fact]
    public void Decode_IacSequenceSplitAcrossReads_IsReassembled()
    {
        var codec = new TelnetCodec();
        var negotiations = new List<TelnetNegotiation>();

        codec.Decode([(byte)'a', Iac], negotiations).Should().Be("a");
        codec.Decode([Do], negotiations).Should().BeEmpty();
        codec.Decode([OptNaws, (byte)'b'], negotiations).Should().Be("b");

        negotiations.Should().Equal(new TelnetNegotiation(Do, OptNaws));
    }

    [Fact]
    public void Decode_EscapedIac_IsDataAndSubnegotiationIsSkipped()
    {
        var codec = new TelnetCodec();
        var negotiations = new List<TelnetNegotiation>();
        byte[] data = [Iac, Sb, 24, 1, Iac, Se, (byte)'x'];

        codec.Decode(data, negotiations).Should().Be("x");
        // 0xFF is not valid UTF-8 on its own; it must reach the decoder rather than be dropped.
        codec.Decode([Iac, Iac], negotiations).Should().Be("�");
        negotiations.Should().BeEmpty();
    }

    [Fact]
    public void Decode_Utf8CharacterSplitAcrossReads_IsDecodedOnce()
    {
        var codec = new TelnetCodec();
        var bytes = Encoding.UTF8.GetBytes("é€");
        var negotiations = new List<TelnetNegotiation>();

        var first = codec.Decode(bytes.AsSpan(0, 3), negotiations);
        var second = codec.Decode(bytes.AsSpan(3), negotiations);

        (first + second).Should().Be("é€");
    }

    [Fact]
    public void EncodeInput_DoublesIacAndPadsBareCarriageReturn()
    {
        EncodeInput([(byte)'l', (byte)'s', (byte)'\r']).Should().Equal((byte)'l', (byte)'s', (byte)'\r', 0);
        EncodeInput([(byte)'\r', (byte)'\n']).Should().Equal((byte)'\r', (byte)'\n');
        EncodeInput([Iac]).Should().Equal(Iac, Iac);
    }

    [Fact]
    public void EncodeWindowSize_EscapesIacInSizeBytes()
    {
        EncodeWindowSize(80, 24).Should().Equal(Iac, Sb, OptNaws, 0, 80, 0, 24, Iac, Se);
        EncodeWindowSize(255, 24).Should().Equal(Iac, Sb, OptNaws, 0, Iac, Iac, 0, 24, Iac, Se);
    }

    [Fact]
    public void Respond_AcceptsEchoOnceAndRefusesUnknownOptionsOnce()
    {
        var codec = new TelnetCodec();

        codec.Respond(new(Will, OptEcho), 80, 24).Should().Equal(Iac, Do, OptEcho);
        codec.Respond(new(Will, OptEcho), 80, 24).Should().BeEmpty("a repeated WILL must not be re-acknowledged");
        codec.Respond(new(Do, 24), 80, 24).Should().Equal(Iac, Wont, 24);
        codec.Respond(new(Do, 24), 80, 24).Should().BeEmpty();
        codec.Respond(new(Wont, 99), 80, 24).Should().BeEmpty("refusing an option we never enabled needs no reply");
    }

    [Fact]
    public void Respond_DoNaws_AfterOffer_SendsSizeWithoutReOffering()
    {
        var codec = new TelnetCodec();
        codec.OfferWindowSize();

        var reply = codec.Respond(new(Do, OptNaws), 120, 40);

        reply.Should().Equal(EncodeWindowSize(120, 40));
        codec.WindowSizeEnabled.Should().BeTrue();
    }

    [Fact]
    public void Respond_DoNaws_WithoutOffer_AgreesAndSendsSize()
    {
        var reply = new TelnetCodec().Respond(new(Do, OptNaws), 80, 24);

        reply.Should().Equal([Iac, Will, OptNaws, .. EncodeWindowSize(80, 24)]);
    }
}
