using System.Globalization;
using System.Text;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Config.Serializers.Csv
{
    /// <summary>
    /// Writes connections in the legacy mRemoteNG CSV format: semicolon separated, no quoting,
    /// semicolons removed from values, every data field terminated by ';', CRLF line breaks and
    /// children written before their parent folder.
    /// </summary>
    /// <remarks>
    /// Two defects of the legacy writer are fixed so the columns line up with their headers:
    /// the legacy header joins "RedirectDiskDrivesCustom" and "RedirectPorts" into one column
    /// (a missing ';'), and wrote the InheritRedirectAudioCapture/InheritRdpVersion/InheritUserViaAPI
    /// values in a different order than their headers. The legacy importer reads columns by name,
    /// so it reads files written here correctly; <see cref="CsvConnectionsDeserializerMremotengFormat"/>
    /// also undoes both defects when it reads a legacy file.
    /// </remarks>
    public class CsvConnectionsSerializerMremotengFormat : ISerializer<ConnectionInfo, string>
    {
        internal const string LineBreak = "\r\n";

        internal const string LeadingHeader = "Name;Id;Parent;NodeType;Description;Icon;Panel;TabColor;ConnectionFrameColor;";

        internal const string MainHeader =
            "Hostname;Port;VmId;Protocol;SSHTunnelConnectionName;OpeningCommand;SSHOptions;PuttySession;ConnectToConsole;UseCredSsp;UseRestrictedAdmin;UseRCG;UseVmId;UseEnhancedMode;RenderingEngine;RDPAuthenticationLevel;" +
            "LoadBalanceInfo;Colors;Resolution;AutomaticResize;DisplayWallpaper;DisplayThemes;EnableFontSmoothing;EnableDesktopComposition;DisableFullWindowDrag;DisableMenuAnimations;DisableCursorShadow;DisableCursorBlinking;" +
            "CacheBitmaps;RedirectDiskDrives;RedirectDiskDrivesCustom;RedirectPorts;RedirectPrinters;RedirectClipboard;RedirectSmartCards;RedirectSound;RedirectKeys;" +
            "PreExtApp;PostExtApp;MacAddress;UserField;EnvironmentTags;ExtApp;Favorite;VNCCompression;VNCEncoding;VNCAuthMode;VNCProxyType;VNCProxyIP;" +
            "VNCProxyPort;VNCProxyUsername;VNCProxyPassword;VNCColors;VNCSmartSizeMode;VNCViewOnly;RDGatewayUsageMethod;RDGatewayHostname;" +
            "RDGatewayUseConnectionCredentials;RDGatewayUsername;RDGatewayPassword;RDGatewayDomain;RDGatewayExternalCredentialProvider;RDGatewayUserViaAPI;RedirectAudioCapture;RdpVersion;RDPStartProgram;RDPStartProgramWorkDir;UserViaAPI;EC2InstanceId;EC2Region;ExternalCredentialProvider;ExternalAddressProvider;";

        internal const string InheritanceHeader =
            "InheritCacheBitmaps;InheritColors;InheritDescription;InheritDisplayThemes;InheritDisplayWallpaper;" +
            "InheritEnableFontSmoothing;InheritEnableDesktopComposition;InheritDisableFullWindowDrag;InheritDisableMenuAnimations;InheritDisableCursorShadow;InheritDisableCursorBlinking;InheritDomain;InheritIcon;InheritPanel;InheritTabColor;InheritConnectionFrameColor;InheritPassword;InheritPort;" +
            "InheritProtocol;InheritSSHTunnelConnectionName;InheritOpeningCommand;InheritSSHOptions;InheritPuttySession;InheritRedirectDiskDrives;InheritRedirectDiskDrivesCustom;InheritRedirectKeys;InheritRedirectPorts;InheritRedirectPrinters;" +
            "InheritRedirectClipboard;InheritRedirectSmartCards;InheritRedirectSound;InheritResolution;InheritAutomaticResize;" +
            "InheritUseConsoleSession;InheritUseCredSsp;InheritUseRestrictedAdmin;InheritUseRCG;InheritUseVmId;InheritUseEnhancedMode;InheritVmId;InheritRenderingEngine;InheritUsername;" +
            "InheritRDPAuthenticationLevel;InheritLoadBalanceInfo;InheritPreExtApp;InheritPostExtApp;InheritMacAddress;InheritUserField;" +
            "InheritEnvironmentTags;InheritFavorite;InheritExtApp;InheritVNCCompression;InheritVNCEncoding;InheritVNCAuthMode;InheritVNCProxyType;InheritVNCProxyIP;" +
            "InheritVNCProxyPort;InheritVNCProxyUsername;InheritVNCProxyPassword;InheritVNCColors;InheritVNCSmartSizeMode;InheritVNCViewOnly;" +
            "InheritRDGatewayUsageMethod;InheritRDGatewayHostname;InheritRDGatewayUseConnectionCredentials;InheritRDGatewayUsername;" +
            "InheritRDGatewayPassword;InheritRDGatewayDomain;InheritRDGatewayExternalCredentialProvider;InheritRDGatewayUserViaAPI;InheritRDPAlertIdleTimeout;InheritRDPMinutesToIdleTimeout;InheritSoundQuality;InheritUserViaAPI;" +
            "InheritRedirectAudioCapture;InheritRdpVersion;InheritExternalCredentialProvider";

        private readonly SaveFilter _saveFilter;

        public CsvConnectionsSerializerMremotengFormat(SaveFilter? saveFilter = null)
        {
            _saveFilter = saveFilter ?? new SaveFilter();
        }

        public string Serialize(ConnectionTreeModel connectionTreeModel)
        {
            ArgumentNullException.ThrowIfNull(connectionTreeModel);
            return Serialize(connectionTreeModel.RootNode);
        }

        /// <summary>Serializes the node and everything below it. A root node itself is not written.</summary>
        public string Serialize(ConnectionInfo serializationTarget)
        {
            ArgumentNullException.ThrowIfNull(serializationTarget);
            var sb = new StringBuilder();
            WriteHeader(sb);
            SerializeNodesRecursive(serializationTarget, sb);
            return sb.ToString();
        }

        private void WriteHeader(StringBuilder sb)
        {
            sb.Append(LeadingHeader);
            if (_saveFilter.SaveUsername)
                sb.Append("Username;");
            if (_saveFilter.SavePassword)
                sb.Append("Password;");
            if (_saveFilter.SaveDomain)
                sb.Append("Domain;");
            sb.Append(MainHeader);
            if (_saveFilter.SaveInheritance)
                sb.Append(InheritanceHeader);
        }

        private void SerializeNodesRecursive(ConnectionInfo node, StringBuilder sb)
        {
            if (node is ContainerInfo container)
            {
                foreach (var child in container.Children)
                    SerializeNodesRecursive(child, sb);
            }

            if (node is RootNodeInfo)
                return;

            SerializeConnectionInfo(node, sb);
        }

        private void SerializeConnectionInfo(ConnectionInfo con, StringBuilder sb)
        {
            sb.Append(LineBreak);
            Field(sb, con.Name);
            Field(sb, con.ConstantID);
            // Like the legacy writer this may name a parent that is not in the file (the root node,
            // or the parent of an exported folder); readers attach such nodes to the top level.
            Field(sb, con.Parent?.ConstantID ?? "");
            Field(sb, con.GetTreeNodeType());
            Field(sb, con.Description);
            Field(sb, con.Icon);
            Field(sb, con.Panel);
            Field(sb, con.TabColor);
            Field(sb, con.ConnectionFrameColor);

            if (_saveFilter.SaveUsername)
                Field(sb, con.Username);
            if (_saveFilter.SavePassword)
                Field(sb, con.Password);
            if (_saveFilter.SaveDomain)
                Field(sb, con.Domain);

            Field(sb, con.Hostname);
            Field(sb, con.Port);
            Field(sb, con.VmId);
            Field(sb, con.Protocol);
            Field(sb, con.SSHTunnelConnectionName);
            Field(sb, con.OpeningCommand);
            Field(sb, con.SSHOptions);
            Field(sb, con.PuttySession);
            Field(sb, con.UseConsoleSession);
            Field(sb, con.UseCredSsp);
            Field(sb, con.UseRestrictedAdmin);
            Field(sb, con.UseRCG);
            Field(sb, con.UseVmId);
            Field(sb, con.UseEnhancedMode);
            Field(sb, con.RenderingEngine);
            Field(sb, con.RDPAuthenticationLevel);
            Field(sb, con.LoadBalanceInfo);
            Field(sb, con.Colors);
            Field(sb, con.Resolution);
            Field(sb, con.AutomaticResize);
            Field(sb, con.DisplayWallpaper);
            Field(sb, con.DisplayThemes);
            Field(sb, con.EnableFontSmoothing);
            Field(sb, con.EnableDesktopComposition);
            Field(sb, con.DisableFullWindowDrag);
            Field(sb, con.DisableMenuAnimations);
            Field(sb, con.DisableCursorShadow);
            Field(sb, con.DisableCursorBlinking);
            Field(sb, con.CacheBitmaps);
            Field(sb, con.RedirectDiskDrives);
            Field(sb, con.RedirectDiskDrivesCustom);
            Field(sb, con.RedirectPorts);
            Field(sb, con.RedirectPrinters);
            Field(sb, con.RedirectClipboard);
            Field(sb, con.RedirectSmartCards);
            Field(sb, con.RedirectSound);
            Field(sb, con.RedirectKeys);
            Field(sb, con.PreExtApp);
            Field(sb, con.PostExtApp);
            Field(sb, con.MacAddress);
            Field(sb, con.UserField);
            Field(sb, con.EnvironmentTags);
            Field(sb, con.ExtApp);
            Field(sb, con.Favorite);
            Field(sb, con.VNCCompression);
            Field(sb, con.VNCEncoding);
            Field(sb, con.VNCAuthMode);
            Field(sb, con.VNCProxyType);
            Field(sb, con.VNCProxyIP);
            Field(sb, con.VNCProxyPort);
            Field(sb, con.VNCProxyUsername);
            Field(sb, _saveFilter.SavePassword ? con.VNCProxyPassword : "");
            Field(sb, con.VNCColors);
            Field(sb, con.VNCSmartSizeMode);
            Field(sb, con.VNCViewOnly);
            Field(sb, con.RDGatewayUsageMethod);
            Field(sb, con.RDGatewayHostname);
            Field(sb, con.RDGatewayUseConnectionCredentials);
            Field(sb, con.RDGatewayUsername);
            Field(sb, _saveFilter.SavePassword ? con.RDGatewayPassword : "");
            Field(sb, con.RDGatewayDomain);
            Field(sb, con.RDGatewayExternalCredentialProvider);
            Field(sb, con.RDGatewayUserViaAPI);
            Field(sb, con.RedirectAudioCapture);
            Field(sb, con.RdpVersion);
            Field(sb, con.RDPStartProgram);
            Field(sb, con.RDPStartProgramWorkDir);
            Field(sb, con.UserViaAPI);
            Field(sb, con.EC2InstanceId);
            Field(sb, con.EC2Region);
            Field(sb, con.ExternalCredentialProvider);
            Field(sb, con.ExternalAddressProvider);

            if (!_saveFilter.SaveInheritance)
                return;

            var inh = con.Inheritance;
            Field(sb, inh.CacheBitmaps);
            Field(sb, inh.Colors);
            Field(sb, inh.Description);
            Field(sb, inh.DisplayThemes);
            Field(sb, inh.DisplayWallpaper);
            Field(sb, inh.EnableFontSmoothing);
            Field(sb, inh.EnableDesktopComposition);
            Field(sb, inh.DisableFullWindowDrag);
            Field(sb, inh.DisableMenuAnimations);
            Field(sb, inh.DisableCursorShadow);
            Field(sb, inh.DisableCursorBlinking);
            Field(sb, inh.Domain);
            Field(sb, inh.Icon);
            Field(sb, inh.Panel);
            Field(sb, inh.TabColor);
            Field(sb, inh.ConnectionFrameColor);
            Field(sb, inh.Password);
            Field(sb, inh.Port);
            Field(sb, inh.Protocol);
            Field(sb, inh.SSHTunnelConnectionName);
            Field(sb, inh.OpeningCommand);
            Field(sb, inh.SSHOptions);
            Field(sb, inh.PuttySession);
            Field(sb, inh.RedirectDiskDrives);
            Field(sb, inh.RedirectDiskDrivesCustom);
            Field(sb, inh.RedirectKeys);
            Field(sb, inh.RedirectPorts);
            Field(sb, inh.RedirectPrinters);
            Field(sb, inh.RedirectClipboard);
            Field(sb, inh.RedirectSmartCards);
            Field(sb, inh.RedirectSound);
            Field(sb, inh.Resolution);
            Field(sb, inh.AutomaticResize);
            Field(sb, inh.UseConsoleSession);
            Field(sb, inh.UseCredSsp);
            Field(sb, inh.UseRestrictedAdmin);
            Field(sb, inh.UseRCG);
            Field(sb, inh.UseVmId);
            Field(sb, inh.UseEnhancedMode);
            Field(sb, inh.VmId);
            Field(sb, inh.RenderingEngine);
            Field(sb, inh.Username);
            Field(sb, inh.RDPAuthenticationLevel);
            Field(sb, inh.LoadBalanceInfo);
            Field(sb, inh.PreExtApp);
            Field(sb, inh.PostExtApp);
            Field(sb, inh.MacAddress);
            Field(sb, inh.UserField);
            Field(sb, inh.EnvironmentTags);
            Field(sb, inh.Favorite);
            Field(sb, inh.ExtApp);
            Field(sb, inh.VNCCompression);
            Field(sb, inh.VNCEncoding);
            Field(sb, inh.VNCAuthMode);
            Field(sb, inh.VNCProxyType);
            Field(sb, inh.VNCProxyIP);
            Field(sb, inh.VNCProxyPort);
            Field(sb, inh.VNCProxyUsername);
            Field(sb, inh.VNCProxyPassword);
            Field(sb, inh.VNCColors);
            Field(sb, inh.VNCSmartSizeMode);
            Field(sb, inh.VNCViewOnly);
            Field(sb, inh.RDGatewayUsageMethod);
            Field(sb, inh.RDGatewayHostname);
            Field(sb, inh.RDGatewayUseConnectionCredentials);
            Field(sb, inh.RDGatewayUsername);
            Field(sb, inh.RDGatewayPassword);
            Field(sb, inh.RDGatewayDomain);
            Field(sb, inh.RDGatewayExternalCredentialProvider);
            Field(sb, inh.RDGatewayUserViaAPI);
            Field(sb, inh.RDPAlertIdleTimeout);
            Field(sb, inh.RDPMinutesToIdleTimeout);
            Field(sb, inh.SoundQuality);
            Field(sb, inh.UserViaAPI);
            Field(sb, inh.RedirectAudioCapture);
            Field(sb, inh.RdpVersion);
            Field(sb, inh.ExternalCredentialProvider);
        }

        /// <summary>
        /// The format has no quoting, so the separator is removed from values (as the legacy writer did)
        /// and line breaks are replaced by spaces so a value cannot split a row.
        /// </summary>
        private static void Field(StringBuilder sb, object? value)
        {
            var text = value switch
            {
                null => "",
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? ""
            };
            sb.Append(text.Replace(";", "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' '));
            sb.Append(';');
        }
    }
}
