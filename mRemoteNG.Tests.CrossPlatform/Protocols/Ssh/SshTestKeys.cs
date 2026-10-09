using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Ssh;

/// <summary>
/// Public keys generated with ssh-keygen, with the fingerprints ssh-keygen -lf -E sha256 printed
/// for them, and a known_hosts file hashed by ssh-keygen -H.
/// </summary>
internal static class SshTestKeys
{
    public const string Ed25519 = "AAAAC3NzaC1lZDI1NTE5AAAAIH20kSx9qTybyI8QHL8s4PqgjxDmBvWy7Bk5dDO//Vg3";
    public const string Ed25519Fingerprint = "SHA256:QWY4RF1mF8HGmwJVow326OQh9bPGzf7/SFTYLyqmxHw";

    public const string Ecdsa256 = "AAAAE2VjZHNhLXNoYTItbmlzdHAyNTYAAAAIbmlzdHAyNTYAAABBBK9ZkhqWPgmw2HtHxII0hl1aqPFBk9zEioMmkQxEXClF4d5qdZDCk7bdC9g28ocHDGUxjb+FKOUELLoHG+6fHJU=";
    public const string Ecdsa256Fingerprint = "SHA256:uW+bW1U4f/n10GPSeA5oiDtGm0Qtrx/MlWsprRXmDrk";

    public const string Rsa = "AAAAB3NzaC1yc2EAAAADAQABAAABAQDndsFG9tqJJhC4ScXog42xO1SkrHHVWRLYvx06304kDBlKGvTVtIZVSL7z4hLG6DpgjfYCR9YSx+UfL4pXkZTsltAPqBHVIZUkPwWqlRFqGdgMwWzbTj5cpz2RY37iNrkJDVxRuyUE+F6QbhhriBUgq1oCqlHEeLFL7jb7wW1YaUChsQ8n+hOxR5+4CpmR+3QaBRzFQ07sbpiR8QfDTkYPh9kBjU5cVMXWdkLQ0BeXj9zaFZ/hKwPRZK2IhCvMpPFSBtaWLfvpWhYS0BRMJ7NjIkj2EVMyawv/MZ8GDlvdZPHt5WLsXta/5KmiCzYBWIM9oL4TYfrMbHAKQMn4bnOn";
    public const string RsaFingerprint = "SHA256:+1EhqW6MPoWbz+wR01OlD6WKSFbBezN9svusxrnY8YY";

    /// <summary>"example.com" (ed25519) and "[example.com]:2222" (ecdsa), hashed by ssh-keygen -H.</summary>
    public const string HashedKnownHosts =
        "|1|T8rU6J4e5EpW3y3Rbe0kN8OFUdg=|tGPQyZnTOsmKN3eZLyaTer9PWQk= ssh-ed25519 " + Ed25519 + "\n" +
        "|1|6heinHRriPnxN1kZKpJb5uVYDwo=|C0iZdSjK7MSdnG9xPcobk0Bg9BU= ecdsa-sha2-nistp256 " + Ecdsa256 + "\n";

    public static HostKeyInfo Key(string host, int port, string base64) =>
        new(host, port, Convert.FromBase64String(base64));
}
