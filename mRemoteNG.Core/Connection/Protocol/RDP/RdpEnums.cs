namespace mRemoteNG.Core.Connection.Protocol.RDP
{
    public enum AuthenticationLevel
    {
        NoAuth = 0,
        AuthRequired = 1,
        WarnOnFailedAuth = 2
    }

    public enum RDGatewayUsageMethod
    {
        Never = 0,
        Always = 1,
        Detect = 2
    }

    public enum RDGatewayUseConnectionCredentials
    {
        No = 0,
        Yes = 1,
        SmartCard = 2,
        ExternalCredentialProvider = 3,
        AccessToken = 4
    }

    public enum RDPColors
    {
        Colors256 = 8,
        Colors15Bit = 15,
        Colors16Bit = 16,
        Colors24Bit = 24,
        Colors32Bit = 32
    }

    public enum RDPDiskDrives
    {
        None,
        Local,
        All,
        Custom
    }

    public enum RDPResolutions
    {
        FitToWindow,
        Fullscreen,
        SmartSize,
        Res800x600,
        Res1024x768,
        Res1152x864,
        Res1280x800,
        Res1280x1024,
        Res1366x768,
        Res1440x900,
        Res1600x900,
        Res1600x1200,
        Res1680x1050,
        Res1920x1080,
        Res1920x1200,
        Res2048x1536,
        Res2560x1440,
        Res2560x1600,
        Res2560x2048,
        Res3840x2160
    }

    public enum RDPSounds
    {
        BringToThisComputer = 0,
        LeaveAtRemoteComputer = 1,
        DoNotPlay = 2
    }

    public enum RDPSoundQuality
    {
        Dynamic = 0,
        Medium = 1,
        High = 2
    }

    public enum RdpVersion
    {
        Rdc6,
        Rdc7,
        Rdc8,
        Rdc9,
        Rdc10,
        Rdc11,
        Highest = 1000
    }

    [Flags]
    public enum RDPPerformanceFlags
    {
        DisableWallpaper = 0x1,
        DisableFullWindowDrag = 0x2,
        DisableMenuAnimations = 0x4,
        DisableThemes = 0x8,
        DisableCursorShadow = 0x20,
        DisableCursorBlinking = 0x40,
        EnableFontSmoothing = 0x80,
        EnableDesktopComposition = 0x100
    }

    public enum RdpNetworkConnectionType
    {
        Modem = 1,
        BroadbandLow = 2,
        Satellite = 3,
        BroadbandHigh = 4,
        Wan = 5,
        Lan = 6,
        AutoDetect = 7
    }
}
