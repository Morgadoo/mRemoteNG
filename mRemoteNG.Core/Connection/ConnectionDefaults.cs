using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Connection.Protocol.VNC;

namespace mRemoteNG.Core.Connection
{
    /// <summary>Defaults and validation rules for connections created or edited in the UI.</summary>
    public static class ConnectionDefaults
    {
        /// <summary>
        /// Applies the legacy application's "new connection" defaults (Settings.settings ConDefault*)
        /// that differ from the CLR defaults of <see cref="ConnectionInfo"/>.
        /// </summary>
        public static T ApplyNewConnectionDefaults<T>(T info) where T : ConnectionInfo
        {
            ArgumentNullException.ThrowIfNull(info);
            info.Icon = "mRemoteNG";
            info.Panel = "General";
            info.Resolution = RDPResolutions.FitToWindow;
            info.AutomaticResize = true;
            info.Colors = RDPColors.Colors16Bit;
            info.UseCredSsp = true;
            info.RedirectSound = RDPSounds.DoNotPlay;
            info.RDGatewayUseConnectionCredentials = RDGatewayUseConnectionCredentials.Yes;
            info.VNCSmartSizeMode = VncSmartSizeMode.SmartSAspect;
            return info;
        }

        /// <summary>
        /// Port to use after the protocol changes from <paramref name="oldProtocol"/> to <paramref name="newProtocol"/>:
        /// the new protocol's default when the current port is unset or still the old protocol's default,
        /// otherwise the user's custom port is kept.
        /// </summary>
        public static int PortAfterProtocolChange(ProtocolType oldProtocol, ProtocolType newProtocol, int currentPort)
        {
            if (currentPort <= 0 || currentPort == ConnectionInfo.GetDefaultPort(oldProtocol))
                return ConnectionInfo.GetDefaultPort(newProtocol);
            return currentPort;
        }

        /// <summary>True when the protocol uses a network port at all.</summary>
        public static bool UsesPort(ProtocolType protocol) => ConnectionInfo.GetDefaultPort(protocol) > 0;

        /// <summary>Validates a hostname and port for <paramref name="protocol"/>; returns null when valid.</summary>
        public static string? ValidateHostname(ProtocolType protocol, string? hostname)
        {
            var host = hostname?.Trim() ?? "";
            if (host.Length == 0)
                return ProtocolFeature.SupportBlankHostname(protocol) ? null : "Hostname is required.";

            if (host.Any(char.IsWhiteSpace))
                return "Hostname must not contain spaces.";

            switch (protocol)
            {
                // HTTP(S) accepts full URLs; AnyDesk takes an ID or alias@ad; WSL takes a distribution name.
                case ProtocolType.HTTP or ProtocolType.HTTPS:
                    return Uri.TryCreate(host.Contains("://") ? host : "http://" + host, UriKind.Absolute, out _)
                        ? null : "Not a valid host name or URL.";
                case ProtocolType.AnyDesk or ProtocolType.WSL or ProtocolType.IntApp:
                    return null;
            }

            var bare = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
            return Uri.CheckHostName(bare) == UriHostNameType.Unknown
                ? "Not a valid host name or IP address."
                : null;
        }

        public static string? ValidatePort(ProtocolType protocol, int port)
        {
            if (!UsesPort(protocol))
                return port is >= 0 and <= 65535 ? null : "Port must be between 0 and 65535.";
            return port is >= 1 and <= 65535 ? null : "Port must be between 1 and 65535.";
        }
    }
}
