using System.Globalization;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Vnc.Rfb;
using Keys = mRemoteNG.Protocols.Abstractions.ConnectionParametersFactory.Keys;

namespace mRemoteNG.Protocols.Vnc;

/// <summary>How a VNC session authenticates when the server offers several methods.</summary>
public enum VncAuthenticationMode
{
    /// <summary>VNC password (falls back to Apple / MS-Logon when that is all the server offers).</summary>
    Vnc,

    /// <summary>UltraVNC MS-Logon: a Windows account (DOMAIN\user and password).</summary>
    Windows,

    /// <summary>Apple Remote Desktop: a macOS account (user name and password).</summary>
    AppleRemoteDesktop,
}

/// <summary>Colour depth requested from the server.</summary>
public enum VncColourDepth
{
    /// <summary>24-bit colour (32 bits per pixel).</summary>
    Full,

    /// <summary>16-bit "high colour" (RGB 565).</summary>
    High,

    /// <summary>8-bit, 256 colours (BGR 233).</summary>
    Low,
}

/// <summary>The VNC options of one session, read from <see cref="ConnectionParameters.Extras"/>.</summary>
public sealed record VncSessionSettings
{
    public VncScaling Scaling { get; init; } = VncScaling.Fit;
    public bool ViewOnly { get; init; }

    /// <summary>RFB encoding to prefer, or null for the client's default order.</summary>
    public int? PreferredEncoding { get; init; }

    /// <summary>Compression level 0-9 (pseudo-encoding), or null to leave it to the server.</summary>
    public int? CompressionLevel { get; init; }

    /// <summary>JPEG quality 0-9; enables lossy JPEG in Tight. Null keeps the session lossless.</summary>
    public int? JpegQuality { get; init; }

    public VncColourDepth ColourDepth { get; init; } = VncColourDepth.Full;
    public VncAuthenticationMode AuthMode { get; init; } = VncAuthenticationMode.Vnc;
    public VncProxySettings? Proxy { get; init; }

    public IReadOnlyList<int> Encodings => RfbClientOptions.BuildEncodings(PreferredEncoding, CompressionLevel, JpegQuality);

    public PixelFormat PixelFormat => ColourDepth switch
    {
        VncColourDepth.High => PixelFormat.Rgb565,
        VncColourDepth.Low => PixelFormat.Bgr233,
        _ => PixelFormat.Bgra32,
    };

    /// <summary>Security types in order of preference for <see cref="AuthMode"/>.</summary>
    public IReadOnlyList<byte> SecurityTypes => AuthMode switch
    {
        VncAuthenticationMode.Windows => [RfbSecurityType.MsLogon2, RfbSecurityType.VncAuthentication, RfbSecurityType.None, RfbSecurityType.AppleRemoteDesktop],
        VncAuthenticationMode.AppleRemoteDesktop => [RfbSecurityType.AppleRemoteDesktop, RfbSecurityType.VncAuthentication, RfbSecurityType.None, RfbSecurityType.MsLogon2],
        _ => RfbClientOptions.DefaultSecurityTypes,
    };

    /// <summary>User name for account-based authentication: "DOMAIN\user" for MS-Logon when a domain is set.</summary>
    public static string? AccountName(ConnectionParameters parameters, VncAuthenticationMode mode) =>
        mode == VncAuthenticationMode.Windows && !string.IsNullOrEmpty(parameters.Domain) && !string.IsNullOrEmpty(parameters.Username)
            ? $"{parameters.Domain}\\{parameters.Username}"
            : parameters.Username;

    public static VncSessionSettings FromParameters(ConnectionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var extras = parameters.Extras;
        string? Get(string key) => extras.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        return new VncSessionSettings
        {
            Scaling = VncProtocol.ParseScaling(Get(Keys.VncScaling)),
            ViewOnly = string.Equals(Get(Keys.VncViewOnly), "true", StringComparison.OrdinalIgnoreCase),
            PreferredEncoding = ParseEncoding(Get(Keys.VncEncoding)),
            CompressionLevel = ParseLevel(Get(Keys.VncCompression)),
            JpegQuality = ParseLevel(Get(Keys.VncJpegQuality)),
            ColourDepth = Get(Keys.VncColors)?.ToLowerInvariant() switch
            {
                "16" or "high" => VncColourDepth.High,
                "8" or "low" => VncColourDepth.Low,
                _ => VncColourDepth.Full,
            },
            AuthMode = Get(Keys.VncAuthMode)?.ToLowerInvariant() switch
            {
                "windows" or "mslogon" => VncAuthenticationMode.Windows,
                "ard" or "apple" => VncAuthenticationMode.AppleRemoteDesktop,
                _ => VncAuthenticationMode.Vnc,
            },
            Proxy = ParseProxy(Get(Keys.VncProxyType), Get(Keys.VncProxyHost), Get(Keys.VncProxyPort),
                Get(Keys.VncProxyUsername), extras.GetValueOrDefault(Keys.VncProxyPassword)),
        };
    }

    /// <summary>Accepts an encoding name (raw, rre, corre, hextile, zlib, tight, zrle) or number.</summary>
    public static int? ParseEncoding(string? value) => value?.ToLowerInvariant() switch
    {
        null => null,
        "raw" => RfbEncoding.Raw,
        "rre" => RfbEncoding.Rre,
        "corre" => RfbEncoding.CoRre,
        "hextile" => RfbEncoding.Hextile,
        // ZlibHex (UltraVNC) is not implemented; Zlib is the closest supported encoding.
        "zlib" or "zlibhex" => RfbEncoding.Zlib,
        "tight" => RfbEncoding.Tight,
        "zrle" => RfbEncoding.Zrle,
        var text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        _ => null,
    };

    private static int? ParseLevel(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) && level is >= 0 and <= 9
            ? level
            : null;

    private static VncProxySettings? ParseProxy(string? type, string? host, string? port, string? username, string? password)
    {
        var kind = type?.ToLowerInvariant() switch
        {
            "http" => VncProxyKind.Http,
            "socks5" or "socks" => VncProxyKind.Socks5,
            "ultravnc" or "repeater" => VncProxyKind.UltraVncRepeater,
            _ => VncProxyKind.None,
        };
        if (kind == VncProxyKind.None) return null;
        var portNumber = int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p > 0
            ? p
            : DefaultProxyPort(kind);
        return new VncProxySettings(kind, host ?? "", portNumber, username, string.IsNullOrEmpty(password) ? null : password);
    }

    /// <summary>Usual ports: HTTP proxy 8080, SOCKS 1080, UltraVNC repeater viewer port 5901.</summary>
    public static int DefaultProxyPort(VncProxyKind kind) => kind switch
    {
        VncProxyKind.Http => 8080,
        VncProxyKind.Socks5 => 1080,
        VncProxyKind.UltraVncRepeater => 5901,
        _ => 0,
    };
}
