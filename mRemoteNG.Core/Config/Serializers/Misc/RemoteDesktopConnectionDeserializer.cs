using System.Globalization;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Config.Serializers.Misc
{
    /// <summary>
    /// Reads a Microsoft Remote Desktop Connection file (.rdp, lines of "name:type:value")
    /// into a single RDP connection under the root.
    /// </summary>
    /// <remarks>
    /// Differences from the legacy importer: "disable wallpaper"/"disable themes" are no longer
    /// inverted (1 means the wallpaper is hidden), and "disable full window drag", "disable menu anims",
    /// "authentication level", "audiocapturemode" and "shell working directory" are read too.
    /// Saved passwords ("password 51") are DPAPI-encrypted and are not imported.
    /// </remarks>
    public class RemoteDesktopConnectionDeserializer : IDeserializer<string, ConnectionTreeModel>
    {
        // .rdp file settings: https://learn.microsoft.com/windows-server/remote/remote-desktop-services/clients/rdp-files

        public ConnectionTreeModel Deserialize(string rdcFileContent)
        {
            var root = new RootNodeInfo(RootNodeType.Connection);
            var connectionInfo = ConnectionDefaults.NewConnection(ProtocolType.RDP);

            foreach (var line in rdcFileContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimStart('﻿').Split(':', 3);
                if (parts.Length < 3)
                    continue;

                SetConnectionInfoParameter(connectionInfo, parts[0].Trim(), parts[2].Trim());
            }

            if (string.IsNullOrEmpty(connectionInfo.Name) || connectionInfo.Name == "New Connection")
                connectionInfo.Name = string.IsNullOrEmpty(connectionInfo.Hostname) ? "New Connection" : connectionInfo.Hostname;

            root.AddChild(connectionInfo);
            return new ConnectionTreeModel(root);
        }

        private static void SetConnectionInfoParameter(ConnectionInfo connectionInfo, string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "full address":
                    SetAddress(connectionInfo, value);
                    break;
                case "server port":
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                        connectionInfo.Port = port;
                    break;
                case "username":
                    connectionInfo.Username = value;
                    break;
                case "domain":
                    connectionInfo.Domain = value;
                    break;
                case "session bpp":
                    switch (value)
                    {
                        case "8":
                            connectionInfo.Colors = RDPColors.Colors256;
                            break;
                        case "15":
                            connectionInfo.Colors = RDPColors.Colors15Bit;
                            break;
                        case "16":
                            connectionInfo.Colors = RDPColors.Colors16Bit;
                            break;
                        case "24":
                            connectionInfo.Colors = RDPColors.Colors24Bit;
                            break;
                        case "32":
                            connectionInfo.Colors = RDPColors.Colors32Bit;
                            break;
                    }
                    break;
                case "bitmapcachepersistenable":
                    connectionInfo.CacheBitmaps = value == "1";
                    break;
                case "screen mode id":
                    connectionInfo.Resolution = value == "2" ? RDPResolutions.Fullscreen : RDPResolutions.FitToWindow;
                    break;
                case "connect to console":
                case "administrative session":
                    connectionInfo.UseConsoleSession = value == "1";
                    break;
                case "disable wallpaper":
                    connectionInfo.DisplayWallpaper = value != "1";
                    break;
                case "disable themes":
                    connectionInfo.DisplayThemes = value != "1";
                    break;
                case "disable full window drag":
                    connectionInfo.DisableFullWindowDrag = value == "1";
                    break;
                case "disable menu anims":
                    connectionInfo.DisableMenuAnimations = value == "1";
                    break;
                case "allow font smoothing":
                    connectionInfo.EnableFontSmoothing = value == "1";
                    break;
                case "allow desktop composition":
                    connectionInfo.EnableDesktopComposition = value == "1";
                    break;
                case "keyboardhook":
                    connectionInfo.RedirectKeys = value == "1";
                    break;
                case "redirectsmartcards":
                    connectionInfo.RedirectSmartCards = value == "1";
                    break;
                case "redirectdrives":
                    connectionInfo.RedirectDiskDrives = value == "1" ? RDPDiskDrives.Local : RDPDiskDrives.None;
                    break;
                case "redirectdrivescustom":
                    connectionInfo.RedirectDiskDrivesCustom = value;
                    break;
                case "redirectcomports":
                    connectionInfo.RedirectPorts = value == "1";
                    break;
                case "redirectprinters":
                    connectionInfo.RedirectPrinters = value == "1";
                    break;
                case "redirectclipboard":
                    connectionInfo.RedirectClipboard = value == "1";
                    break;
                case "audiomode":
                    switch (value)
                    {
                        case "0":
                            connectionInfo.RedirectSound = RDPSounds.BringToThisComputer;
                            break;
                        case "1":
                            connectionInfo.RedirectSound = RDPSounds.LeaveAtRemoteComputer;
                            break;
                        case "2":
                            connectionInfo.RedirectSound = RDPSounds.DoNotPlay;
                            break;
                    }
                    break;
                case "audiocapturemode":
                case "redirectaudiocapture":
                    connectionInfo.RedirectAudioCapture = value == "1";
                    break;
                case "authentication level":
                    switch (value)
                    {
                        case "0":
                            connectionInfo.RDPAuthenticationLevel = AuthenticationLevel.NoAuth;
                            break;
                        case "1":
                            connectionInfo.RDPAuthenticationLevel = AuthenticationLevel.AuthRequired;
                            break;
                        case "2":
                            connectionInfo.RDPAuthenticationLevel = AuthenticationLevel.WarnOnFailedAuth;
                            break;
                    }
                    break;
                case "loadbalanceinfo":
                    connectionInfo.LoadBalanceInfo = value;
                    break;
                case "gatewayusagemethod":
                    switch (value)
                    {
                        case "0":
                        case "4":
                            connectionInfo.RDGatewayUsageMethod = RDGatewayUsageMethod.Never;
                            break;
                        case "1":
                            connectionInfo.RDGatewayUsageMethod = RDGatewayUsageMethod.Always;
                            break;
                        case "2":
                            connectionInfo.RDGatewayUsageMethod = RDGatewayUsageMethod.Detect;
                            break;
                    }
                    break;
                case "gatewayhostname":
                    connectionInfo.RDGatewayHostname = value;
                    break;
                case "gatewaycredentialssource":
                    switch (value)
                    {
                        case "0":
                            connectionInfo.RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.ExternalCredentialProvider;
                            break;
                        case "1":
                            connectionInfo.RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.SmartCard;
                            break;
                        case "2":
                            connectionInfo.RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.Yes;
                            break;
                        case "3":
                        case "4":
                            // Both require the user to enter gateway credentials manually.
                            connectionInfo.RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.No;
                            break;
                        case "5":
                            connectionInfo.RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.AccessToken;
                            break;
                    }
                    break;
                case "gatewayaccesstoken":
                    connectionInfo.RDGatewayAccessToken = value;
                    break;
                case "alternate shell":
                    connectionInfo.RDPStartProgram = value;
                    break;
                case "shell working directory":
                    connectionInfo.RDPStartProgramWorkDir = value;
                    break;
            }
        }

        private static void SetAddress(ConnectionInfo connectionInfo, string value)
        {
            if (Uri.TryCreate("dummyscheme" + Uri.SchemeDelimiter + value, UriKind.Absolute, out var uri)
                && !string.IsNullOrEmpty(uri.Host))
            {
                connectionInfo.Hostname = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.Host;
                if (uri.Port != -1)
                    connectionInfo.Port = uri.Port;
            }
            else
            {
                connectionInfo.Hostname = value;
            }
        }
    }
}
