using System.Reflection;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Connection
{
    /// <summary>
    /// Manages per-property inheritance flags for connections.
    /// Each boolean property corresponds to a property on ConnectionInfo.
    /// When true, the value is inherited from the parent container.
    /// </summary>
    public class ConnectionInfoInheritance
    {
        private ConnectionInfoInheritance? _tempInheritanceStorage;

        // Display
        public bool Description { get; set; }
        public bool Icon { get; set; }
        public bool Panel { get; set; }
        public bool Color { get; set; }
        public bool TabColor { get; set; }
        public bool ConnectionFrameColor { get; set; }

        // Connection
        public bool ExternalCredentialProvider { get; set; }
        public bool UserViaAPI { get; set; }
        public bool Username { get; set; }
        public bool VmId { get; set; }
        public bool Password { get; set; }
        public bool Domain { get; set; }
        public bool Port { get; set; }
        public bool SSHTunnelConnectionName { get; set; }
        public bool OpeningCommand { get; set; }

        // Protocol
        public bool Protocol { get; set; }
        public bool RdpVersion { get; set; }
        public bool ExtApp { get; set; }
        public bool PuttySession { get; set; }
        public bool SSHOptions { get; set; }
        public bool RDPAuthenticationLevel { get; set; }
        public bool RDPMinutesToIdleTimeout { get; set; }
        public bool RDPAlertIdleTimeout { get; set; }
        public bool LoadBalanceInfo { get; set; }
        public bool RenderingEngine { get; set; }
        public bool UseConsoleSession { get; set; }
        public bool UseCredSsp { get; set; }
        public bool UseRestrictedAdmin { get; set; }
        public bool UseRCG { get; set; }
        public bool UseVmId { get; set; }
        public bool UseEnhancedMode { get; set; }

        // RD Gateway
        public bool RDGatewayUsageMethod { get; set; }
        public bool RDGatewayHostname { get; set; }
        public bool RDGatewayUseConnectionCredentials { get; set; }
        public bool RDGatewayUsername { get; set; }
        public bool RDGatewayPassword { get; set; }
        public bool RDGatewayDomain { get; set; }
        public bool RDGatewayExternalCredentialProvider { get; set; }
        public bool RDGatewayUserViaAPI { get; set; }

        // Appearance
        public bool Resolution { get; set; }
        public bool AutomaticResize { get; set; }
        public bool Colors { get; set; }
        public bool CacheBitmaps { get; set; }
        public bool DisplayWallpaper { get; set; }
        public bool DisplayThemes { get; set; }
        public bool EnableFontSmoothing { get; set; }
        public bool EnableDesktopComposition { get; set; }
        public bool DisableFullWindowDrag { get; set; }
        public bool DisableMenuAnimations { get; set; }
        public bool DisableCursorShadow { get; set; }
        public bool DisableCursorBlinking { get; set; }

        // Redirect
        public bool RedirectKeys { get; set; }
        public bool RedirectDiskDrives { get; set; }
        public bool RedirectDiskDrivesCustom { get; set; }
        public bool RedirectPrinters { get; set; }
        public bool RedirectClipboard { get; set; }
        public bool RedirectPorts { get; set; }
        public bool RedirectSmartCards { get; set; }
        public bool RedirectSound { get; set; }
        public bool SoundQuality { get; set; }
        public bool RedirectAudioCapture { get; set; }

        // Start Program
        public bool RDPStartProgram { get; set; }
        public bool RDPStartProgramWorkDir { get; set; }

        // Misc
        public bool PreExtApp { get; set; }
        public bool PostExtApp { get; set; }
        public bool MacAddress { get; set; }
        public bool UserField { get; set; }
        public bool EnvironmentTags { get; set; }
        public bool Favorite { get; set; }

        // VNC
        public bool VNCCompression { get; set; }
        public bool VNCEncoding { get; set; }
        public bool VNCAuthMode { get; set; }
        public bool VNCProxyType { get; set; }
        public bool VNCProxyIP { get; set; }
        public bool VNCProxyPort { get; set; }
        public bool VNCProxyUsername { get; set; }
        public bool VNCProxyPassword { get; set; }
        public bool VNCColors { get; set; }
        public bool VNCSmartSizeMode { get; set; }
        public bool VNCViewOnly { get; set; }

        // Computed
        public bool EverythingInherited
        {
            get => EverythingIsInherited();
            set => SetAllValues(value);
        }

        public ConnectionInfo Parent { get; private set; }

        /// <summary>
        /// Inheritance is active when the parent is not a root node
        /// and not a direct child of a root node.
        /// </summary>
        public bool InheritanceActive => !(Parent is RootNodeInfo || Parent?.Parent is RootNodeInfo);

        public ConnectionInfoInheritance(ConnectionInfo parent, bool ignoreDefaultInheritance = false)
        {
            Parent = parent;
        }

        public ConnectionInfoInheritance Clone(ConnectionInfo parent)
        {
            var newInheritance = (ConnectionInfoInheritance)MemberwiseClone();
            newInheritance.Parent = parent;
            return newInheritance;
        }

        public void EnableInheritance()
        {
            if (_tempInheritanceStorage != null)
            {
                SetAllValues(_tempInheritanceStorage);
                _tempInheritanceStorage = null;
            }
        }

        public void DisableInheritance()
        {
            _tempInheritanceStorage = Clone(Parent);
            TurnOffInheritanceCompletely();
        }

        public void TurnOnInheritanceCompletely()
        {
            SetAllValues(true);
        }

        public void TurnOffInheritanceCompletely()
        {
            SetAllValues(false);
        }

        public IEnumerable<PropertyInfo> GetProperties()
        {
            return typeof(ConnectionInfoInheritance).GetProperties()
                .Where(FilterProperty);
        }

        public IEnumerable<string> GetEnabledInheritanceProperties()
        {
            if (!InheritanceActive)
                return [];

            return GetProperties()
                .Where(p => (bool)p.GetValue(this)!)
                .Select(p => p.Name);
        }

        private bool EverythingIsInherited()
        {
            return GetProperties().All(p => (bool)p.GetValue(this)!);
        }

        private void SetAllValues(bool value)
        {
            foreach (var property in GetProperties())
            {
                if (property.PropertyType == typeof(bool))
                    property.SetValue(this, value);
            }
        }

        private void SetAllValues(ConnectionInfoInheritance other)
        {
            foreach (var property in GetProperties())
            {
                property.SetValue(this, property.GetValue(other));
            }
        }

        private static bool FilterProperty(PropertyInfo propertyInfo)
        {
            string[] exclusions = { nameof(EverythingInherited), nameof(Parent), nameof(InheritanceActive) };
            return !exclusions.Contains(propertyInfo.Name);
        }
    }
}
