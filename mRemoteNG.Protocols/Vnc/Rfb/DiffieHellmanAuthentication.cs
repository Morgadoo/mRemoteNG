using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace mRemoteNG.Protocols.Vnc.Rfb;

/// <summary>
/// Apple Remote Desktop authentication (security type 30), as spoken by macOS Screen Sharing.
/// <para>
/// The server sends a Diffie-Hellman generator, key length, prime and its public key. The client picks a private
/// key, derives the shared secret, and sends the user name and password — each in a 64-byte NUL-terminated field —
/// encrypted with AES-128-ECB under MD5(shared secret), followed by its own public key.
/// </para>
/// </summary>
public static class AppleRemoteDesktopAuthentication
{
    public const int CredentialFieldLength = 64;

    /// <summary>Server parameters as received: generator, prime modulus and server public key (big-endian).</summary>
    public sealed record Challenge(int Generator, byte[] Prime, byte[] ServerPublicKey)
    {
        public int KeyLength => Prime.Length;
    }

    public static Challenge ReadChallenge(RfbReader reader)
    {
        int generator = reader.ReadUInt16();
        int keyLength = reader.ReadUInt16();
        if (keyLength is < 8 or > 1024)
            throw new RfbProtocolException($"Apple Remote Desktop key length {keyLength} is not plausible.");
        var prime = reader.ReadBytes(keyLength);
        var serverPublic = reader.ReadBytes(keyLength);
        return new Challenge(generator, prime, serverPublic);
    }

    /// <summary>Builds the client's reply: 128 bytes of encrypted credentials followed by the client public key.</summary>
    /// <param name="privateKey">For tests; normally a random key is generated.</param>
    public static byte[] ComputeResponse(Challenge challenge, string username, string password, byte[]? privateKey = null)
    {
        var keyLength = challenge.KeyLength;
        var prime = ToBigInteger(challenge.Prime);
        var serverPublic = ToBigInteger(challenge.ServerPublicKey);
        var priv = ToBigInteger(privateKey ?? RandomNumberGenerator.GetBytes(keyLength)) % (prime - 2) + 1;

        var clientPublic = BigInteger.ModPow(challenge.Generator, priv, prime);
        var secret = BigInteger.ModPow(serverPublic, priv, prime);
        var aesKey = MD5.HashData(ToFixedBytes(secret, keyLength));

        var credentials = RandomNumberGenerator.GetBytes(2 * CredentialFieldLength);
        WriteField(credentials.AsSpan(0, CredentialFieldLength), username);
        WriteField(credentials.AsSpan(CredentialFieldLength, CredentialFieldLength), password);

        using var aes = Aes.Create();
        aes.Key = aesKey;
        var encrypted = aes.EncryptEcb(credentials, PaddingMode.None);

        var response = new byte[encrypted.Length + keyLength];
        encrypted.CopyTo(response, 0);
        ToFixedBytes(clientPublic, keyLength).CopyTo(response, encrypted.Length);
        return response;
    }

    /// <summary>UTF-8 text, NUL-terminated; the rest of the field keeps its random filler.</summary>
    private static void WriteField(Span<byte> field, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var length = Math.Min(bytes.Length, field.Length - 1);
        bytes.AsSpan(0, length).CopyTo(field);
        field[length] = 0;
    }

    internal static BigInteger ToBigInteger(ReadOnlySpan<byte> bigEndian) =>
        new(bigEndian, isUnsigned: true, isBigEndian: true);

    /// <summary>Big-endian, left-padded with zeros to <paramref name="length"/> bytes.</summary>
    internal static byte[] ToFixedBytes(BigInteger value, int length)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == length) return bytes;
        if (bytes.Length > length)
            return bytes[^length..];
        var padded = new byte[length];
        bytes.CopyTo(padded, length - bytes.Length);
        return padded;
    }
}

/// <summary>
/// UltraVNC MS-Logon II authentication (security type 113): the server checks a Windows account.
/// <para>
/// The server sends three 64-bit big-endian numbers: generator, modulus and its public value. The client replies
/// with its public value, then a 256-byte user name and a 64-byte password, each encrypted with DES in CBC mode
/// using the 8-byte shared secret as both key and IV (with VNC's bit-mirrored DES key, as in VNC authentication).
/// </para>
/// </summary>
public static class MsLogon2Authentication
{
    public const int UsernameLength = 256;
    public const int PasswordLength = 64;

    /// <summary>Builds the 8 + 256 + 64 byte reply.</summary>
    /// <param name="privateKey">For tests; normally random.</param>
    public static byte[] ComputeResponse(ulong generator, ulong modulus, ulong serverPublic, string username,
        string password, ulong? privateKey = null)
    {
        if (modulus < 3)
            throw new RfbProtocolException("MS-Logon II modulus is not plausible.");
        var priv = privateKey ?? BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
        var clientPublic = (ulong)BigInteger.ModPow(generator, priv, modulus);
        var secret = (ulong)BigInteger.ModPow(serverPublic, priv, modulus);

        var key = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(key, secret);

        var response = new byte[8 + UsernameLength + PasswordLength];
        BinaryPrimitives.WriteUInt64BigEndian(response, clientPublic);
        var user = response.AsSpan(8, UsernameLength);
        var pass = response.AsSpan(8 + UsernameLength, PasswordLength);
        WriteField(user, username);
        WriteField(pass, password);
        EncryptCbc(user, key);
        EncryptCbc(pass, key);
        return response;
    }

    /// <summary>
    /// DES-CBC encryption in place with IV = key — UltraVNC's <c>vncEncryptBytes2</c>. The key goes through the same
    /// bit mirroring as VNC authentication because both use the original d3des key setup.
    /// </summary>
    internal static void EncryptCbc(Span<byte> data, ReadOnlySpan<byte> key)
    {
        Span<byte> mirrored = stackalloc byte[8];
        for (var i = 0; i < 8; i++) mirrored[i] = VncAuthentication.ReverseBits(key[i]);
        var schedule = Des.CreateKeySchedule(BinaryPrimitives.ReadUInt64BigEndian(mirrored));

        var chain = BinaryPrimitives.ReadUInt64BigEndian(key);
        for (var offset = 0; offset < data.Length; offset += 8)
        {
            var block = BinaryPrimitives.ReadUInt64BigEndian(data[offset..]) ^ chain;
            chain = Des.EncryptBlock(block, schedule);
            BinaryPrimitives.WriteUInt64BigEndian(data[offset..], chain);
        }
    }

    /// <summary>NUL-terminated, zero-filled (Windows code pages cannot be assumed, so non-ASCII becomes UTF-8).</summary>
    private static void WriteField(Span<byte> field, string text)
    {
        field.Clear();
        var bytes = Encoding.UTF8.GetBytes(text);
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length - 1)).CopyTo(field);
    }
}
