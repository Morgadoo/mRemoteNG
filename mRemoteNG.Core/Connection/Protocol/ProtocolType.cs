namespace mRemoteNG.Core.Connection.Protocol
{
    public enum ProtocolType
    {
        RDP = 0,
        VNC = 1,
        SSH1 = 2,
        SSH2 = 3,
        Telnet = 4,
        Rlogin = 5,
        RAW = 6,
        HTTP = 7,
        HTTPS = 8,
        PowerShell = 10,
        ARD = 11,
        Terminal = 12,
        WSL = 13,
        AnyDesk = 14,
        IntApp = 20
    }

    public static class ProtocolFeature
    {
        public static bool SupportBlankHostname(ProtocolType protocolType)
        {
            return protocolType is ProtocolType.IntApp or ProtocolType.PowerShell
                or ProtocolType.WSL or ProtocolType.Terminal;
        }
    }
}
