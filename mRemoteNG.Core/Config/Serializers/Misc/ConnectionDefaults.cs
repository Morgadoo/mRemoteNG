using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.Http;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Connection.Protocol.VNC;
using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Config.Serializers.Misc
{
    /// <summary>
    /// Creates nodes for importers with the same defaults the XML loader uses for missing attributes
    /// (which match the legacy app's defaults for a new connection). A bare <see cref="ConnectionInfo"/>
    /// leaves enums such as <see cref="ConnectionInfo.Colors"/> at invalid zero values.
    /// </summary>
    public static class ConnectionDefaults
    {
        public static ConnectionInfo NewConnection(ProtocolType protocol = ProtocolType.RDP, string? id = null)
        {
            var connection = new ConnectionInfo(id ?? Guid.NewGuid().ToString());
            Apply(connection, protocol);
            return connection;
        }

        public static ContainerInfo NewContainer(string name, string? id = null)
        {
            var container = new ContainerInfo(id ?? Guid.NewGuid().ToString());
            Apply(container, ProtocolType.RDP);
            container.Name = name;
            container.IsExpanded = true;
            return container;
        }

        public static void Apply(ConnectionInfo node, ProtocolType protocol = ProtocolType.RDP)
        {
            node.Icon = "mRemoteNG";
            node.Panel = "General";
            node.Protocol = protocol;
            node.Port = ConnectionInfo.GetDefaultPort(protocol);
            node.PuttySession = "Default Settings";
            node.VaultOpenbaoSecretEngine = VaultOpenbaoSecretEngine.Kv;

            node.RdpVersion = RdpVersion.Highest;
            node.Resolution = RDPResolutions.FitToWindow;
            node.AutomaticResize = true;
            node.Colors = RDPColors.Colors24Bit;
            node.CacheBitmaps = true;
            node.RedirectClipboard = true;
            node.RedirectDiskDrives = RDPDiskDrives.None;
            node.RedirectSound = RDPSounds.DoNotPlay;
            node.SoundQuality = RDPSoundQuality.Dynamic;
            node.UseCredSsp = true;
            node.RDPAuthenticationLevel = AuthenticationLevel.NoAuth;
            node.RenderingEngine = RenderingEngine.EdgeChromium;
            node.RDGatewayUsageMethod = RDGatewayUsageMethod.Never;
            node.RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.Yes;

            node.VNCCompression = VncCompression.CompNone;
            node.VNCEncoding = VncEncoding.EncTight;
            node.VNCAuthMode = VncAuthMode.AuthVNC;
            node.VNCProxyType = VncProxyType.ProxyNone;
            node.VNCColors = VncColors.ColNormal;
            node.VNCSmartSizeMode = VncSmartSizeMode.SmartSAspect;
        }
    }
}
