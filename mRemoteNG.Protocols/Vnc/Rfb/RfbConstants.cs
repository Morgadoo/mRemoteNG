namespace mRemoteNG.Protocols.Vnc.Rfb;

/// <summary>RFB (RFC 6143) wire constants.</summary>
public static class RfbEncoding
{
    public const int Raw = 0;
    public const int CopyRect = 1;
    public const int Rre = 2;
    public const int CoRre = 4;
    public const int Hextile = 5;
    public const int Zlib = 6;
    public const int Tight = 7;
    public const int Zrle = 16;

    // Pseudo-encodings
    public const int Cursor = -239;
    public const int DesktopSize = -223;
    public const int LastRect = -224;
    public const int ExtendedDesktopSize = -308;

    /// <summary>Compression level 0 pseudo-encoding; level n is <c>CompressLevel0 + n</c> (n = 0…9).</summary>
    public const int CompressLevel0 = -256;

    /// <summary>JPEG quality level 0 pseudo-encoding; level n is <c>QualityLevel0 + n</c> (n = 0…9).</summary>
    public const int QualityLevel0 = -32;

    public static int CompressLevel(int level) => CompressLevel0 + Math.Clamp(level, 0, 9);

    public static int QualityLevel(int level) => QualityLevel0 + Math.Clamp(level, 0, 9);
}

public static class RfbSecurityType
{
    public const byte Invalid = 0;
    public const byte None = 1;
    public const byte VncAuthentication = 2;

    /// <summary>Apple Remote Desktop: Diffie-Hellman + AES-128 encrypted user name and password (macOS Screen Sharing).</summary>
    public const byte AppleRemoteDesktop = 30;

    /// <summary>UltraVNC MS-Logon II: Diffie-Hellman + DES-CBC encrypted Windows user name and password.</summary>
    public const byte MsLogon2 = 113;
}

internal static class RfbClientMessage
{
    public const byte SetPixelFormat = 0;
    public const byte SetEncodings = 2;
    public const byte FramebufferUpdateRequest = 3;
    public const byte KeyEvent = 4;
    public const byte PointerEvent = 5;
    public const byte ClientCutText = 6;
}

internal static class RfbServerMessage
{
    public const byte FramebufferUpdate = 0;
    public const byte SetColourMapEntries = 1;
    public const byte Bell = 2;
    public const byte ServerCutText = 3;
}

/// <summary>Pointer button bits of an RFB PointerEvent (button N is bit N-1).</summary>
[Flags]
public enum RfbButtons : byte
{
    None = 0,
    Left = 1,
    Middle = 2,
    Right = 4,
    WheelUp = 8,
    WheelDown = 16,
    WheelLeft = 32,
    WheelRight = 64,
}

/// <summary>The server violated the protocol or sent something this client cannot handle.</summary>
public class RfbProtocolException : Exception
{
    public RfbProtocolException(string message) : base(message) { }
}

/// <summary>The server refused the connection or rejected the credentials.</summary>
public sealed class RfbAuthenticationException : RfbProtocolException
{
    public RfbAuthenticationException(string message) : base(message) { }
}
