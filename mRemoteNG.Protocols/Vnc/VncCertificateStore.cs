using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using mRemoteNG.Core.App.Info;

namespace mRemoteNG.Protocols.Vnc;

/// <summary>Whether a VNC server's TLS certificate is trusted, and a note for the user.</summary>
public readonly record struct VncCertificateVerdict(bool IsTrusted, string Message);

/// <summary>Decides whether the TLS certificate of a VNC server (VeNCrypt X509 sub-types) is trusted.</summary>
public interface IVncCertificateVerifier
{
    VncCertificateVerdict Verify(string host, int port, X509Certificate2 certificate, SslPolicyErrors errors);
}

/// <summary>
/// Trusts certificates the system trusts, and otherwise pins the certificate on first use (as VNC servers mostly
/// use self-signed certificates): a later connection to the same host and port must present the same certificate.
/// Pins are lines of <c>host:port SHA256:fingerprint</c> in <see cref="FilePath"/>.
/// </summary>
public sealed class VncCertificateStore : IVncCertificateVerifier
{
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _sessionPins = new(StringComparer.OrdinalIgnoreCase);

    public VncCertificateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public string FilePath { get; }

    public static VncCertificateStore CreateDefault() =>
        new(Path.Combine(ApplicationPaths.SettingsDirectory, "vnc_certificates"));

    /// <summary>SHA-256 of the DER certificate, formatted like OpenSSH fingerprints.</summary>
    public static string Fingerprint(X509Certificate2 certificate) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(certificate.RawData)).TrimEnd('=');

    public static string FormatHost(string host, int port) => $"{host.Trim().ToLowerInvariant()}:{port}";

    public VncCertificateVerdict Verify(string host, int port, X509Certificate2 certificate, SslPolicyErrors errors)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        if (errors == SslPolicyErrors.None)
            return new VncCertificateVerdict(true, "The server's TLS certificate is trusted by the system.");

        var name = FormatHost(host, port);
        var fingerprint = Fingerprint(certificate);
        lock (_lock)
        {
            var pinned = ReadPins().GetValueOrDefault(name) ?? _sessionPins.GetValueOrDefault(name);
            if (string.Equals(pinned, fingerprint, StringComparison.Ordinal))
                return new VncCertificateVerdict(true, $"The server's TLS certificate matches the one trusted before ({fingerprint}).");
            if (pinned is not null)
            {
                return new VncCertificateVerdict(false,
                    $"The TLS certificate of {name} has changed: it is now {fingerprint}, but {pinned} was trusted before. "
                    + "This can mean that someone is intercepting the connection. If the change is expected, remove the line "
                    + $"for {name} from {FilePath}.");
            }

            _sessionPins[name] = fingerprint;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{name} {fingerprint}{Environment.NewLine}");
                return new VncCertificateVerdict(true,
                    $"Trusted the TLS certificate {fingerprint} of {name} on first use ({DescribeErrors(errors)}); later connections must present the same certificate.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new VncCertificateVerdict(true,
                    $"Trusted the TLS certificate {fingerprint} of {name} for this session; it could not be saved: {ex.Message}");
            }
        }
    }

    private static string DescribeErrors(SslPolicyErrors errors) =>
        (errors & SslPolicyErrors.RemoteCertificateChainErrors) != 0
            ? "it is self-signed or its issuer is not trusted"
            : "its name does not match the host";

    private Dictionary<string, string> ReadPins()
    {
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(FilePath)) return pins;
        foreach (var line in File.ReadLines(FilePath))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2) pins[parts[0]] = parts[1];
        }
        return pins;
    }
}
