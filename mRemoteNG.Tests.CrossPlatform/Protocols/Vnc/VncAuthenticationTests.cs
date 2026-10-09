using FluentAssertions;
using mRemoteNG.Protocols.Vnc.Rfb;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

public class VncAuthenticationTests
{
    private static readonly byte[] Challenge = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    [Fact]
    public void Des_EncryptBlock_MatchesStandardTestVector()
    {
        // The classic worked example (key 133457799BBCDFF1); confirmed with `openssl enc -des-ecb`.
        var schedule = Des.CreateKeySchedule(0x133457799BBCDFF1);

        Des.EncryptBlock(0x0123456789ABCDEF, schedule).Should().Be(0x85E813540F0AB405);
    }

    // Expected values computed independently with OpenSSL (legacy provider) using the bit-reversed key.
    [Theory]
    [InlineData("password", "b866924125c8eebb9debc1db61c538e2")]
    [InlineData("abc", "9c22b4f2088c3465a1562c4b9d6edb04")]
    [InlineData("", "491e890de9ace932838a49792f2213f3")]
    [InlineData("longpassword123", "5931256585fd62106d317e09fc963baf")]
    public void ComputeResponse_MatchesReferenceImplementation(string password, string expectedHex)
    {
        VncAuthentication.ComputeResponse(password, Challenge)
            .Should().Equal(Convert.FromHexString(expectedHex));
    }

    [Fact]
    public void ComputeResponse_OnlyFirstEightCharactersMatter()
    {
        VncAuthentication.ComputeResponse("longpass", Challenge)
            .Should().Equal(VncAuthentication.ComputeResponse("longpassword123", Challenge));
    }

    [Theory]
    [InlineData(0x01, 0x80)]
    [InlineData(0x70, 0x0E)]
    [InlineData(0xF0, 0x0F)]
    [InlineData(0xA5, 0xA5)]
    public void ReverseBits_MirrorsByte(byte input, byte expected)
    {
        VncAuthentication.ReverseBits(input).Should().Be(expected);
    }

    [Fact]
    public void ComputeResponse_RejectsWrongChallengeLength()
    {
        var act = () => VncAuthentication.ComputeResponse("x", new byte[8]);

        act.Should().Throw<ArgumentException>();
    }
}
