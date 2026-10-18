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

    /// <summary>UltraVNC: Hextile with zlib-compressed tiles.</summary>
    public const int ZlibHex = 8;

    /// <summary>UltraVNC: Raw pixels compressed with LZO1X.</summary>
    public const int Ultra = 9;

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

    /// <summary>VeNCrypt: a second negotiation of <see cref="VeNCryptSubtype"/>s, most of them wrapped in TLS.</summary>
    public const byte VeNCrypt = 19;

    /// <summary>Apple Remote Desktop: Diffie-Hellman + AES-128 encrypted user name and password (macOS Screen Sharing).</summary>
    public const byte AppleRemoteDesktop = 30;

    /// <summary>UltraVNC MS-Logon II: Diffie-Hellman + DES-CBC encrypted Windows user name and password.</summary>
    public const byte MsLogon2 = 113;
}

/// <summary>
/// VeNCrypt sub-types. "TLS" types use anonymous (certificate-less) TLS, "X509" types a server certificate;
/// None (1) and VNC authentication (2) may also be offered as unencrypted sub-types.
/// </summary>
public static class VeNCryptSubtype
{
    public const uint None = RfbSecurityType.None;
    public const uint VncAuthentication = RfbSecurityType.VncAuthentication;

    /// <summary>User name and password in clear text.</summary>
    public const uint Plain = 256;
    public const uint TlsNone = 257;
    public const uint TlsVnc = 258;
    public const uint TlsPlain = 259;
    public const uint X509None = 260;
    public const uint X509Vnc = 261;
    public const uint X509Plain = 262;

    public static bool UsesX509(uint subtype) => subtype is X509None or X509Vnc or X509Plain;

    public static bool UsesAnonymousTls(uint subtype) => subtype is TlsNone or TlsVnc or TlsPlain;

    public static string Name(uint subtype) => subtype switch
    {
        None => "None",
        VncAuthentication => "VncAuth",
        Plain => "Plain",
        TlsNone => "TLSNone",
        TlsVnc => "TLSVnc",
        TlsPlain => "TLSPlain",
        X509None => "X509None",
        X509Vnc => "X509Vnc",
        X509Plain => "X509Plain",
        _ => subtype.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

internal static class RfbClientMessage
{
    public const byte SetPixelFormat = 0;
    public const byte SetEncodings = 2;
    public const byte FramebufferUpdateRequest = 3;
    public const byte KeyEvent = 4;
    public const byte PointerEvent = 5;
    public const byte ClientCutText = 6;

    /// <summary>UltraVNC file transfer (same number in both directions).</summary>
    public const byte FileTransfer = 7;

    /// <summary>UltraVNC text chat (same number in both directions).</summary>
    public const byte TextChat = 11;
}

internal static class RfbServerMessage
{
    public const byte FramebufferUpdate = 0;
    public const byte SetColourMapEntries = 1;
    public const byte Bell = 2;
    public const byte ServerCutText = 3;
    public const byte FileTransfer = RfbClientMessage.FileTransfer;
    public const byte TextChat = RfbClientMessage.TextChat;
}

/// <summary>UltraVNC text chat: special lengths that open, close and end the chat instead of carrying text.</summary>
public static class RfbTextChat
{
    public const uint Open = 0xFFFFFFFF;
    public const uint Close = 0xFFFFFFFE;
    public const uint Finished = 0xFFFFFFFD;

    /// <summary>Longest message servers accept (LibVNCServer closes the connection at 4096 bytes).</summary>
    public const int MaxLength = 4095;
}

/// <summary>What a received UltraVNC text chat message means.</summary>
public enum RfbTextChatKind
{
    Text,
    Opened,
    Closed,
    Finished,
}

public sealed record RfbTextChatMessage(RfbTextChatKind Kind, string Text = "");

/// <summary>UltraVNC file transfer content types and parameters (rfbproto.h, file transfer version 2).</summary>
public static class RfbFileTransfer
{
    public const byte DirContentRequest = 1;
    public const byte DirPacket = 2;
    public const byte FileTransferRequest = 3;
    public const byte FileHeader = 4;
    public const byte FilePacket = 5;
    public const byte EndOfFile = 6;
    public const byte AbortFileTransfer = 7;
    public const byte FileTransferOffer = 8;
    public const byte FileAcceptHeader = 9;
    public const byte Command = 10;
    public const byte CommandReturn = 11;
    public const byte FileChecksums = 12;
    public const byte FileTransferAccess = 14;

    // DirContentRequest parameters
    public const byte RequestDirContent = 1;
    public const byte RequestDrivesList = 2;

    // DirPacket / CommandReturn parameters
    public const byte AnswerDirectory = 1;
    public const byte AnswerFile = 2;
    public const byte AnswerDrivesList = 3;
    public const byte AnswerDirCreate = 4;
    public const byte AnswerDirDelete = 5;
    public const byte AnswerFileDelete = 7;
    public const byte AnswerFileRename = 8;

    // Command parameters
    public const byte CommandDirCreate = 1;
    public const byte CommandDirDelete = 2;
    public const byte CommandFileDelete = 4;
    public const byte CommandFileRename = 5;

    /// <summary>Sent in <see cref="AbortFileTransfer"/>'s parameter to ask for access (servers answer <see cref="FileTransferAccess"/>).</summary>
    public const byte ProtocolVersion = 2;

    /// <summary>File data is sent in blocks of this size (before compression).</summary>
    public const int BlockSize = 8192;

    /// <summary>The "size" of a failed request or command.</summary>
    public const uint Error = 0xFFFFFFFF;
}

/// <summary>A received UltraVNC file transfer message.</summary>
public sealed record RfbFileTransferMessage(byte ContentType, byte ContentParam, uint Size, byte[] Data, uint SizeHigh = 0);

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

/// <summary>
/// The server offered VeNCrypt, but none of its sub-types can be used. The connection is lost (RFB has no way to go
/// back), but a new connection without VeNCrypt may succeed when <see cref="OtherSecurityTypesOffered"/> is true.
/// </summary>
public sealed class RfbVeNCryptUnsupportedException : RfbProtocolException
{
    public RfbVeNCryptUnsupportedException(string message, bool otherSecurityTypesOffered) : base(message) =>
        OtherSecurityTypesOffered = otherSecurityTypesOffered;

    public bool OtherSecurityTypesOffered { get; }
}
