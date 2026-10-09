using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace mRemoteNG.Protocols.Vnc;

/// <summary>How the VNC connection reaches the server.</summary>
public enum VncProxyKind
{
    None,

    /// <summary>HTTP proxy, using the CONNECT method (optionally with Basic authentication).</summary>
    Http,

    /// <summary>SOCKS5 proxy (RFC 1928), optionally with user name / password authentication (RFC 1929).</summary>
    Socks5,

    /// <summary>
    /// UltraVNC repeater: the viewer connects to the repeater and names the server as "host:port" (mode I)
    /// or "ID:nnnn" (mode II, where the server connected to the repeater with the same ID).
    /// </summary>
    UltraVncRepeater,
}

public sealed record VncProxySettings(VncProxyKind Kind, string Host, int Port, string? Username = null, string? Password = null);

/// <summary>Opens the TCP stream for a VNC session, directly or through a proxy or repeater.</summary>
public static class VncTransport
{
    /// <summary>Size of the server identification block an UltraVNC repeater expects.</summary>
    public const int RepeaterIdLength = 250;

    /// <summary>The fake RFB banner an UltraVNC repeater greets viewers with.</summary>
    public const string RepeaterBanner = "RFB 000.000\n";

    /// <summary>
    /// Connects to <paramref name="host"/>:<paramref name="port"/>, through <paramref name="proxy"/> when given.
    /// The returned client's stream is positioned at the VNC server's RFB banner.
    /// </summary>
    /// <exception cref="IOException">The proxy refused or failed the connection.</exception>
    public static async Task<TcpClient> ConnectAsync(string host, int port, VncProxySettings? proxy, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            if (proxy is null || proxy.Kind == VncProxyKind.None)
            {
                await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
                return tcp;
            }

            if (string.IsNullOrWhiteSpace(proxy.Host) || proxy.Port is <= 0 or > 65535)
                throw new IOException($"The {Describe(proxy.Kind)} address is not configured (host \"{proxy.Host}\", port {proxy.Port}).");
            try
            {
                await tcp.ConnectAsync(proxy.Host, proxy.Port, ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                throw new IOException($"Could not connect to the {Describe(proxy.Kind)} {proxy.Host}:{proxy.Port}: {ex.Message}", ex);
            }

            var stream = tcp.GetStream();
            // A stalled proxy must not hang the handshake: closing the socket unblocks the reads below.
            await using var registration = ct.Register(tcp.Dispose);
            switch (proxy.Kind)
            {
                case VncProxyKind.Http:
                    await HttpConnectAsync(stream, host, port, proxy, ct).ConfigureAwait(false);
                    break;
                case VncProxyKind.Socks5:
                    await Socks5ConnectAsync(stream, host, port, proxy, ct).ConfigureAwait(false);
                    break;
                case VncProxyKind.UltraVncRepeater:
                    await RepeaterConnectAsync(stream, host, port, ct).ConfigureAwait(false);
                    break;
            }
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static string Describe(VncProxyKind kind) => kind switch
    {
        VncProxyKind.Http => "HTTP proxy",
        VncProxyKind.Socks5 => "SOCKS5 proxy",
        VncProxyKind.UltraVncRepeater => "UltraVNC repeater",
        _ => "proxy",
    };

    // ── HTTP CONNECT ───────────────────────────────────────────────────────

    private static async Task HttpConnectAsync(Stream stream, string host, int port, VncProxySettings proxy, CancellationToken ct)
    {
        var authority = (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{host}]" : host)
            + ":" + port.ToString(CultureInfo.InvariantCulture);
        var request = new StringBuilder()
            .Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(authority).Append("\r\n")
            .Append("User-Agent: mRemoteNG\r\n");
        if (!string.IsNullOrEmpty(proxy.Username))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{proxy.Username}:{proxy.Password}"));
            request.Append("Proxy-Authorization: Basic ").Append(token).Append("\r\n");
        }
        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), ct).ConfigureAwait(false);

        // Read the response headers byte by byte so nothing the server sends after them is consumed.
        var status = await ReadLineAsync(stream, ct).ConfigureAwait(false);
        while ((await ReadLineAsync(stream, ct).ConfigureAwait(false)).Length > 0)
        {
        }

        var parts = status.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(parts[1], out var code))
            throw new IOException($"The HTTP proxy sent an invalid response: \"{status}\".");
        if (code is >= 200 and < 300) return;
        var reason = parts.Length > 2 ? parts[2] : "";
        throw new IOException(code == 407
            ? $"The HTTP proxy requires authentication ({code} {reason}). Check the proxy user name and password."
            : $"The HTTP proxy refused the connection to {authority}: {code} {reason}".TrimEnd());
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var line = new StringBuilder();
        var b = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(b, ct).ConfigureAwait(false) == 0)
                throw new IOException("The HTTP proxy closed the connection.");
            if (b[0] == '\n') break;
            if (b[0] != '\r') line.Append((char)b[0]);
            if (line.Length > 8192) throw new IOException("The HTTP proxy sent an overlong header line.");
        }
        return line.ToString();
    }

    // ── SOCKS5 ─────────────────────────────────────────────────────────────

    private static async Task Socks5ConnectAsync(Stream stream, string host, int port, VncProxySettings proxy, CancellationToken ct)
    {
        var withPassword = !string.IsNullOrEmpty(proxy.Username);
        byte[] greeting = withPassword ? [5, 2, 0, 2] : [5, 1, 0];
        await stream.WriteAsync(greeting, ct).ConfigureAwait(false);
        var choice = await ReadExactAsync(stream, 2, ct).ConfigureAwait(false);
        if (choice[0] != 5)
            throw new IOException("The SOCKS proxy does not speak SOCKS version 5.");

        switch (choice[1])
        {
            case 0:
                break;
            case 2 when withPassword:
            {
                var user = Encoding.UTF8.GetBytes(proxy.Username!);
                var pass = Encoding.UTF8.GetBytes(proxy.Password ?? "");
                if (user.Length > 255 || pass.Length > 255)
                    throw new IOException("The SOCKS5 user name and password are limited to 255 bytes each.");
                var auth = new byte[3 + user.Length + pass.Length];
                auth[0] = 1;
                auth[1] = (byte)user.Length;
                user.CopyTo(auth, 2);
                auth[2 + user.Length] = (byte)pass.Length;
                pass.CopyTo(auth, 3 + user.Length);
                await stream.WriteAsync(auth, ct).ConfigureAwait(false);
                var result = await ReadExactAsync(stream, 2, ct).ConfigureAwait(false);
                if (result[1] != 0)
                    throw new IOException("The SOCKS5 proxy rejected the user name or password.");
                break;
            }
            case 2:
                throw new IOException("The SOCKS5 proxy requires a user name and password.");
            default:
                throw new IOException("The SOCKS5 proxy accepts none of the offered authentication methods.");
        }

        // CONNECT, letting the proxy resolve host names.
        var request = new List<byte> { 5, 1, 0 };
        if (IPAddress.TryParse(host, out var ip))
        {
            request.Add((byte)(ip.AddressFamily == AddressFamily.InterNetworkV6 ? 4 : 1));
            request.AddRange(ip.GetAddressBytes());
        }
        else
        {
            var name = Encoding.ASCII.GetBytes(IdnHost(host));
            if (name.Length > 255) throw new IOException("The host name is too long for SOCKS5.");
            request.Add(3);
            request.Add((byte)name.Length);
            request.AddRange(name);
        }
        request.Add((byte)(port >> 8));
        request.Add((byte)port);
        await stream.WriteAsync(request.ToArray(), ct).ConfigureAwait(false);

        var reply = await ReadExactAsync(stream, 4, ct).ConfigureAwait(false);
        if (reply[0] != 5)
            throw new IOException("The SOCKS5 proxy sent an invalid reply.");
        if (reply[1] != 0)
            throw new IOException($"The SOCKS5 proxy could not connect to {host}:{port}: {SocksError(reply[1])}.");
        var addressLength = reply[3] switch
        {
            1 => 4,
            4 => 16,
            3 => (await ReadExactAsync(stream, 1, ct).ConfigureAwait(false))[0],
            _ => throw new IOException("The SOCKS5 proxy sent an invalid bound address."),
        };
        await ReadExactAsync(stream, addressLength + 2, ct).ConfigureAwait(false);
    }

    private static string IdnHost(string host)
    {
        try { return new IdnMapping().GetAscii(host); }
        catch (ArgumentException) { return host; }
    }

    private static string SocksError(byte code) => code switch
    {
        1 => "general failure",
        2 => "connection not allowed by ruleset",
        3 => "network unreachable",
        4 => "host unreachable",
        5 => "connection refused",
        6 => "TTL expired",
        7 => "command not supported",
        8 => "address type not supported",
        _ => $"error {code}",
    };

    // ── UltraVNC repeater ──────────────────────────────────────────────────

    private static async Task RepeaterConnectAsync(Stream stream, string host, int port, CancellationToken ct)
    {
        var banner = Encoding.ASCII.GetString(await ReadExactAsync(stream, RepeaterBanner.Length, ct).ConfigureAwait(false));
        if (banner != RepeaterBanner)
            throw new IOException($"The UltraVNC repeater sent an unexpected greeting \"{banner.TrimEnd()}\" (expected \"{RepeaterBanner.TrimEnd()}\").");
        await stream.WriteAsync(RepeaterId(host, port), ct).ConfigureAwait(false);
    }

    /// <summary>"ID:nnnn" is passed on as is; any other host becomes "host:port". NUL padded to 250 bytes.</summary>
    public static byte[] RepeaterId(string host, int port)
    {
        var id = host.StartsWith("ID:", StringComparison.OrdinalIgnoreCase)
            ? host
            : $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";
        var bytes = Encoding.ASCII.GetBytes(id);
        if (bytes.Length >= RepeaterIdLength)
            throw new IOException("The repeater target is too long.");
        var block = new byte[RepeaterIdLength];
        bytes.CopyTo(block, 0);
        return block;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        try
        {
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new IOException("The proxy closed the connection.", ex);
        }
        return buffer;
    }
}
