namespace mRemoteNG.Core.Connection.Protocol.VNC
{
    public enum VncCompression
    {
        CompNone = 99,
        Comp0 = 0,
        Comp1 = 1,
        Comp2 = 2,
        Comp3 = 3,
        Comp4 = 4,
        Comp5 = 5,
        Comp6 = 6,
        Comp7 = 7,
        Comp8 = 8,
        Comp9 = 9
    }

    public enum VncEncoding
    {
        EncRaw,
        EncRRE,
        EncCorre,
        EncHextile,
        EncZlib,
        EncTight,
        EncZLibHex,
        EncZRLE
    }

    public enum VncAuthMode
    {
        AuthVNC,
        AuthWin
    }

    public enum VncProxyType
    {
        ProxyNone,
        ProxyHTTP,
        ProxySocks5,
        ProxyUltra
    }

    public enum VncColors
    {
        ColNormal,
        Col8Bit
    }

    public enum VncSmartSizeMode
    {
        SmartSNo,
        SmartSFree,
        SmartSAspect
    }
}
