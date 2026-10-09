using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using mRemoteNG.Protocols.Vnc.Rfb;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary>
/// Apple Remote Desktop and UltraVNC MS-Logon II authentication against an in-process server that performs the
/// server side of each scheme with independent crypto (System.Security.Cryptography AES, BouncyCastle DES).
/// </summary>
public sealed class VncSecurityTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>RFC 2409 Oakley group 2 (1024-bit MODP) prime, as macOS uses for ARD.</summary>
    private static readonly byte[] Oakley2Prime = Convert.FromHexString(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD"
        + "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED"
        + "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE65381FFFFFFFFFFFFFFFF");

    private readonly FakeRfbServer _server = new();
    private readonly TcpClient _tcp = new();
    private RfbClient? _client;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _tcp.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<RfbClient> ConnectAsync(RfbClientOptions options, Func<Task> serverScript)
    {
        await _tcp.ConnectAsync(IPAddress.Loopback, _server.Port);
        await _server.AcceptAsync();
        var clientTask = RfbClient.ConnectAsync(_tcp.GetStream(), options);
        var serverTask = serverScript();
        try
        {
            _client = await clientTask.WaitAsync(Timeout);
        }
        finally
        {
            if (serverTask.IsCompleted) await serverTask;
        }
        await serverTask.WaitAsync(Timeout);
        return _client;
    }

    // ── Apple Remote Desktop ───────────────────────────────────────────────

    [Fact]
    public async Task AppleRemoteDesktop_SendsAesEncryptedCredentials_AndClientPublicKey()
    {
        string user = "", pass = "";
        var client = await ConnectAsync(
            new RfbClientOptions { Username = "alice", Password = "pa55w0rd!" },
            async () =>
            {
                // macOS announces 3.889; the client answers 3.8.
                (await _server.NegotiateVersionAsync("003.889")).Should().Be("RFB 003.008\n");
                await _server.SendAsync(new RfbBytes().U8(2).U8(RfbSecurityType.AppleRemoteDesktop).U8(35));
                (await _server.ReceiveAsync(1))[0].Should().Be(RfbSecurityType.AppleRemoteDesktop);

                var prime = new BigInteger(Oakley2Prime, isUnsigned: true, isBigEndian: true);
                var serverPrivate = new BigInteger(RandomNumberGenerator.GetBytes(64), isUnsigned: true, isBigEndian: true);
                var serverPublic = BigInteger.ModPow(2, serverPrivate, prime);
                await _server.SendAsync(new RfbBytes().U16(2).U16(128).Raw(Oakley2Prime).Raw(Pad(serverPublic, 128)));

                var reply = await _server.ReceiveAsync(128 + 128);
                var clientPublic = new BigInteger(reply.AsSpan(128), isUnsigned: true, isBigEndian: true);
                var secret = BigInteger.ModPow(clientPublic, serverPrivate, prime);
                using var aes = Aes.Create();
                aes.Key = MD5.HashData(Pad(secret, 128));
                var plain = aes.DecryptEcb(reply.AsSpan(0, 128), PaddingMode.None);
                user = Encoding.UTF8.GetString(plain, 0, Array.IndexOf(plain, (byte)0));
                pass = Encoding.UTF8.GetString(plain, 64, Array.IndexOf(plain, (byte)0, 64) - 64);

                await _server.SendAsync(new RfbBytes().U32(0));
                await _server.InitialiseAsync(64, 48, "mac");
            });

        client.SecurityType.Should().Be(RfbSecurityType.AppleRemoteDesktop);
        user.Should().Be("alice");
        pass.Should().Be("pa55w0rd!");
        client.DesktopName.Should().Be("mac");
    }

    [Fact]
    public async Task AppleRemoteDesktop_Rejected_ThrowsWithServerReason()
    {
        var act = () => ConnectAsync(
            new RfbClientOptions { Username = "alice", Password = "wrong" },
            async () =>
            {
                await _server.NegotiateVersionAsync("003.889");
                await _server.SendAsync(new RfbBytes().U8(1).U8(RfbSecurityType.AppleRemoteDesktop));
                await _server.ReceiveAsync(1);
                var serverPublic = BigInteger.ModPow(2, 12345, new BigInteger(Oakley2Prime, true, true));
                await _server.SendAsync(new RfbBytes().U16(2).U16(128).Raw(Oakley2Prime).Raw(Pad(serverPublic, 128)));
                await _server.ReceiveAsync(256);
                await _server.SendAsync(new RfbBytes().U32(1).String("Authentication failed"));
            });

        (await act.Should().ThrowAsync<RfbAuthenticationException>()).WithMessage("*Apple Remote Desktop*Authentication failed*");
    }

    [Fact]
    public async Task AppleRemoteDesktop_WithoutUserName_ExplainsWhatIsMissing()
    {
        var act = () => ConnectAsync(
            new RfbClientOptions { Password = "secret" },
            async () =>
            {
                await _server.NegotiateVersionAsync("003.889");
                await _server.SendAsync(new RfbBytes().U8(1).U8(RfbSecurityType.AppleRemoteDesktop));
                await _server.ReceiveAsync(1);
                await _server.SendAsync(new RfbBytes().U16(2).U16(8).Raw(new byte[16]));
            });

        (await act.Should().ThrowAsync<RfbAuthenticationException>()).WithMessage("*user name*");
    }

    // ── MS-Logon II ────────────────────────────────────────────────────────

    [Fact]
    public async Task MsLogon2_SendsDesCbcEncryptedWindowsCredentials()
    {
        const ulong modulus = 18446744073709551557UL; // largest 64-bit prime
        const ulong generator = 5;
        string user = "", pass = "";

        var client = await ConnectAsync(
            new RfbClientOptions
            {
                Username = @"CORP\bob",
                Password = "Winter2026",
                SecurityTypes = [RfbSecurityType.MsLogon2, RfbSecurityType.VncAuthentication],
            },
            async () =>
            {
                await _server.NegotiateVersionAsync("003.008");
                await _server.SendAsync(new RfbBytes().U8(2).U8(RfbSecurityType.VncAuthentication).U8(RfbSecurityType.MsLogon2));
                (await _server.ReceiveAsync(1))[0].Should().Be(RfbSecurityType.MsLogon2);

                const ulong serverPrivate = 0x1234_5678_9ABC_DEF1;
                var serverPublic = (ulong)BigInteger.ModPow(generator, serverPrivate, modulus);
                await _server.SendAsync(new RfbBytes().Raw(U64(generator)).Raw(U64(modulus)).Raw(U64(serverPublic)));

                var reply = await _server.ReceiveAsync(8 + 256 + 64);
                var clientPublic = BinaryPrimitives.ReadUInt64BigEndian(reply);
                var key = U64((ulong)BigInteger.ModPow(clientPublic, serverPrivate, modulus));
                var userField = DecryptDesCbc(reply.AsSpan(8, 256).ToArray(), key);
                var passField = DecryptDesCbc(reply.AsSpan(8 + 256, 64).ToArray(), key);
                user = Encoding.UTF8.GetString(userField, 0, Array.IndexOf(userField, (byte)0));
                pass = Encoding.UTF8.GetString(passField, 0, Array.IndexOf(passField, (byte)0));

                await _server.SendAsync(new RfbBytes().U32(0));
                await _server.InitialiseAsync(32, 32, "ultravnc");
            });

        client.SecurityType.Should().Be(RfbSecurityType.MsLogon2);
        user.Should().Be(@"CORP\bob");
        pass.Should().Be("Winter2026");
    }

    [Fact]
    public void MsLogon2_EncryptCbc_MatchesBouncyCastleDes()
    {
        var key = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF };
        var plain = Encoding.ASCII.GetBytes("sixteen byte msg");
        var data = (byte[])plain.Clone();

        MsLogon2Authentication.EncryptCbc(data, key);

        DecryptDesCbc(data, key).Should().Equal(plain);
    }

    /// <summary>DES-CBC with IV = key and VNC's bit-mirrored key bytes, decrypted by BouncyCastle.</summary>
    private static byte[] DecryptDesCbc(byte[] data, byte[] key)
    {
        var mirrored = key.Select(Mirror).ToArray();
        var cipher = new CbcBlockCipher(new DesEngine());
        cipher.Init(false, new ParametersWithIV(new KeyParameter(mirrored), key));
        var output = new byte[data.Length];
        for (var offset = 0; offset < data.Length; offset += 8)
            cipher.ProcessBlock(data, offset, output, offset);
        return output;
    }

    private static byte Mirror(byte b)
    {
        var r = 0;
        for (var i = 0; i < 8; i++)
            r |= ((b >> i) & 1) << (7 - i);
        return (byte)r;
    }

    private static byte[] U64(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Pad(BigInteger value, int length)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var padded = new byte[length];
        bytes.CopyTo(padded, length - bytes.Length);
        return padded;
    }

    // ── Choosing a security type ───────────────────────────────────────────

    [Theory]
    [InlineData(new byte[] { 2, 1 }, null, "pw", RfbSecurityType.None)]
    [InlineData(new byte[] { 2, 30 }, null, "pw", RfbSecurityType.VncAuthentication)]
    [InlineData(new byte[] { 30, 2 }, "alice", null, RfbSecurityType.AppleRemoteDesktop)] // no VNC password: use the account
    [InlineData(new byte[] { 30, 2 }, null, null, RfbSecurityType.VncAuthentication)]     // nothing usable: first preference, to name the missing password
    [InlineData(new byte[] { 113 }, "bob", "pw", RfbSecurityType.MsLogon2)]
    [InlineData(new byte[] { 16, 19 }, "bob", "pw", null)]
    public void ChooseSecurityType_PrefersSatisfiableTypes(byte[] offered, string? user, string? password, byte? expected)
    {
        var options = new RfbClientOptions { Username = user, Password = password };
        RfbClient.ChooseSecurityType(offered, options).Should().Be(expected);
    }

    [Fact]
    public void ChooseSecurityType_FollowsTheConfiguredOrder()
    {
        var ard = new RfbClientOptions
        {
            Username = "alice",
            Password = "pw",
            SecurityTypes = [RfbSecurityType.AppleRemoteDesktop, RfbSecurityType.VncAuthentication],
        };
        RfbClient.ChooseSecurityType([RfbSecurityType.VncAuthentication, RfbSecurityType.AppleRemoteDesktop], ard)
            .Should().Be(RfbSecurityType.AppleRemoteDesktop);
    }
}
