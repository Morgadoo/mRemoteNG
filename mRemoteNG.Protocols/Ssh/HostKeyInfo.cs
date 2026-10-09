using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>
/// A server host key as presented during the SSH key exchange.
/// </summary>
public sealed class HostKeyInfo
{
    public HostKeyInfo(string host, int port, byte[] keyBlob)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(keyBlob);

        Host = host;
        Port = port;
        KeyBlob = keyBlob;
        KeyType = ReadKeyType(keyBlob)
            ?? throw new ArgumentException("The host key blob does not start with a key type.", nameof(keyBlob));
    }

    public string Host { get; }
    public int Port { get; }

    /// <summary>Key type from the key blob, e.g. <c>ssh-ed25519</c>, <c>ecdsa-sha2-nistp256</c>, <c>ssh-rsa</c>.</summary>
    public string KeyType { get; }

    /// <summary>The public key in SSH wire format (the base64 part of a known_hosts line).</summary>
    public byte[] KeyBlob { get; }

    public string KeyBase64 => Convert.ToBase64String(KeyBlob);

    /// <summary>OpenSSH-style fingerprint, e.g. <c>SHA256:ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og</c>.</summary>
    public string Fingerprint => ComputeFingerprint(KeyBlob);

    public static string ComputeFingerprint(byte[] keyBlob) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(keyBlob)).TrimEnd('=');

    /// <summary>Reads the leading SSH string (the key type) from a wire-format public key.</summary>
    public static string? ReadKeyType(ReadOnlySpan<byte> keyBlob)
    {
        if (keyBlob.Length < 4)
            return null;
        uint length = BinaryPrimitives.ReadUInt32BigEndian(keyBlob);
        if (length == 0 || length > 64 || length > keyBlob.Length - 4)
            return null;
        return Encoding.ASCII.GetString(keyBlob.Slice(4, (int)length));
    }
}
