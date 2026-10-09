namespace mRemoteNG.Core.Config.Serializers.Sql
{
    /// <summary>Logical column types of the connection tables.</summary>
    public enum SqlColumnKind
    {
        /// <summary>Auto-increment integer (never written by the application).</summary>
        Identity,
        Bool,
        /// <summary>Small integer used as a boolean (legacy <c>Favorite</c> column).</summary>
        TinyInt,
        Int,
        String,
        DateTime,
    }

    /// <summary>One column of <c>tblCons</c>.</summary>
    /// <param name="Size">varchar length (strings only).</param>
    /// <param name="DefaultValue">Literal DEFAULT value from the legacy script, if any.</param>
    /// <param name="IsExtension">Not part of the legacy CREATE TABLE script (added for compatibility).</param>
    public sealed record SqlColumn(
        string Name,
        SqlColumnKind Kind,
        int Size = 0,
        bool Nullable = false,
        string? DefaultValue = null,
        bool IsExtension = false);

    /// <summary>
    /// The connection database schema of the legacy app (<c>SqlDatabaseMetaDataRetriever</c>, version 3.0):
    /// <c>tblCons</c> (one row per connection/folder), <c>tblRoot</c> (name, password check, schema version) and
    /// <c>tblUpdate</c> (time of the last save, polled by other clients).
    /// </summary>
    public static class SqlSchema
    {
        public const string ConnectionsTable = "tblCons";
        public const string RootTable = "tblRoot";
        public const string UpdateTable = "tblUpdate";

        /// <summary>The schema version written to <c>tblRoot.ConfVersion</c>.</summary>
        public static readonly Version CurrentVersion = new(3, 0);

        public static IReadOnlyList<string> Tables { get; } = [ConnectionsTable, RootTable, UpdateTable];

        /// <summary>The columns of <c>tblCons</c>, in the legacy order.</summary>
        public static IReadOnlyList<SqlColumn> ConnectionColumns { get; } =
        [
            new SqlColumn("ID", SqlColumnKind.Identity),
            new SqlColumn("ConstantID", SqlColumnKind.String, Size: 128),
            new SqlColumn("PositionID", SqlColumnKind.Int),
            new SqlColumn("ParentID", SqlColumnKind.String, Size: 128, Nullable: true),
            new SqlColumn("LastChange", SqlColumnKind.DateTime),
            new SqlColumn("Name", SqlColumnKind.String, Size: 128),
            new SqlColumn("Type", SqlColumnKind.String, Size: 32),
            new SqlColumn("Expanded", SqlColumnKind.Bool),
            new SqlColumn("AutomaticResize", SqlColumnKind.Bool, DefaultValue: "1"),
            new SqlColumn("CacheBitmaps", SqlColumnKind.Bool),
            new SqlColumn("Colors", SqlColumnKind.String, Size: 32),
            new SqlColumn("ConnectToConsole", SqlColumnKind.Bool),
            new SqlColumn("Connected", SqlColumnKind.Bool),
            new SqlColumn("Description", SqlColumnKind.String, Size: 1024, Nullable: true),
            new SqlColumn("DisableCursorBlinking", SqlColumnKind.Bool),
            new SqlColumn("DisableCursorShadow", SqlColumnKind.Bool),
            new SqlColumn("DisableFullWindowDrag", SqlColumnKind.Bool),
            new SqlColumn("DisableMenuAnimations", SqlColumnKind.Bool),
            new SqlColumn("DisplayThemes", SqlColumnKind.Bool),
            new SqlColumn("DisplayWallpaper", SqlColumnKind.Bool),
            new SqlColumn("Domain", SqlColumnKind.String, Size: 512, Nullable: true),
            new SqlColumn("EnableDesktopComposition", SqlColumnKind.Bool),
            new SqlColumn("EnableFontSmoothing", SqlColumnKind.Bool),
            new SqlColumn("ExtApp", SqlColumnKind.String, Size: 256, Nullable: true),
            new SqlColumn("Favorite", SqlColumnKind.TinyInt),
            new SqlColumn("Hostname", SqlColumnKind.String, Size: 512, Nullable: true),
            new SqlColumn("Icon", SqlColumnKind.String, Size: 128),
            new SqlColumn("LoadBalanceInfo", SqlColumnKind.String, Size: 1024, Nullable: true),
            new SqlColumn("MacAddress", SqlColumnKind.String, Size: 32, Nullable: true),
            new SqlColumn("OpeningCommand", SqlColumnKind.String, Size: 512, Nullable: true),
            new SqlColumn("Panel", SqlColumnKind.String, Size: 128),
            new SqlColumn("Password", SqlColumnKind.String, Size: 1024, Nullable: true),
            new SqlColumn("Port", SqlColumnKind.Int),
            new SqlColumn("PostExtApp", SqlColumnKind.String, Size: 256, Nullable: true),
            new SqlColumn("PreExtApp", SqlColumnKind.String, Size: 256, Nullable: true),
            new SqlColumn("Protocol", SqlColumnKind.String, Size: 32),
            new SqlColumn("PuttySession", SqlColumnKind.String, Size: 128, Nullable: true),
            new SqlColumn("RDGatewayDomain", SqlColumnKind.String, Size: 512, Nullable: true),
            new SqlColumn("RDGatewayHostname", SqlColumnKind.String, Size: 512, Nullable: true),
            new SqlColumn("RDGatewayPassword", SqlColumnKind.String, Size: 1024, Nullable: true),
            new SqlColumn("RDGatewayUsageMethod", SqlColumnKind.String, Size: 32),
            new SqlColumn("RDGatewayUseConnectionCredentials", SqlColumnKind.String, Size: 32),
            new SqlColumn("RDGatewayUsername", SqlColumnKind.String, Size: 512, Nullable: true),
            new SqlColumn("RDPAlertIdleTimeout", SqlColumnKind.Bool),
            new SqlColumn("RDPAuthenticationLevel", SqlColumnKind.String, Size: 32),
            new SqlColumn("RDPMinutesToIdleTimeout", SqlColumnKind.Int),
            new SqlColumn("RdpVersion", SqlColumnKind.String, Size: 10, Nullable: true),
            new SqlColumn("RedirectAudioCapture", SqlColumnKind.Bool),
            new SqlColumn("RedirectClipboard", SqlColumnKind.Bool),
            new SqlColumn("RedirectDiskDrives", SqlColumnKind.String, Size: 32, Nullable: true),
            new SqlColumn("RedirectDiskDrivesCustom", SqlColumnKind.String, Size: 32, Nullable: true),
            new SqlColumn("RedirectKeys", SqlColumnKind.Bool),
            new SqlColumn("RedirectPorts", SqlColumnKind.Bool),
            new SqlColumn("RedirectPrinters", SqlColumnKind.Bool),
            new SqlColumn("RedirectSmartCards", SqlColumnKind.Bool),
            new SqlColumn("RedirectSound", SqlColumnKind.String, Size: 64),
            new SqlColumn("RenderingEngine", SqlColumnKind.String, Size: 32, Nullable: true),
            new SqlColumn("Resolution", SqlColumnKind.String, Size: 32),
            new SqlColumn("SSHOptions", SqlColumnKind.String, Size: 1024),
            new SqlColumn("SSHTunnelConnectionName", SqlColumnKind.String, Size: 128),
            new SqlColumn("SoundQuality", SqlColumnKind.String, Size: 20),
            new SqlColumn("UseCredSsp", SqlColumnKind.Bool),
            new SqlColumn("UseEnhancedMode", SqlColumnKind.Bool),
            new SqlColumn("UseVmId", SqlColumnKind.Bool),
            new SqlColumn("UserField", SqlColumnKind.String, Size: 256, Nullable: true),
            new SqlColumn("Username", SqlColumnKind.String, Size: 512, Nullable: true),
            new SqlColumn("VNCAuthMode", SqlColumnKind.String, Size: 10, Nullable: true),
            new SqlColumn("VNCColors", SqlColumnKind.String, Size: 10, Nullable: true),
            new SqlColumn("VNCCompression", SqlColumnKind.String, Size: 10, Nullable: true),
            new SqlColumn("VNCEncoding", SqlColumnKind.String, Size: 20, Nullable: true),
            new SqlColumn("VNCProxyIP", SqlColumnKind.String, Size: 128, Nullable: true),
            new SqlColumn("VNCProxyPassword", SqlColumnKind.String, Size: 1024, Nullable: true),
            new SqlColumn("VNCProxyPort", SqlColumnKind.Int, Nullable: true),
            new SqlColumn("VNCProxyType", SqlColumnKind.String, Size: 20, Nullable: true),
            new SqlColumn("VNCProxyUsername", SqlColumnKind.String, Size: 512, Nullable: true),
            new SqlColumn("VNCSmartSizeMode", SqlColumnKind.String, Size: 20, Nullable: true),
            new SqlColumn("VNCViewOnly", SqlColumnKind.Bool),
            new SqlColumn("VmId", SqlColumnKind.String, Size: 512, Nullable: true),
            new SqlColumn("ICAEncryptionStrength", SqlColumnKind.String, Size: 32),
            new SqlColumn("InheritAutomaticResize", SqlColumnKind.Bool),
            new SqlColumn("InheritCacheBitmaps", SqlColumnKind.Bool),
            new SqlColumn("InheritColors", SqlColumnKind.Bool),
            new SqlColumn("InheritDescription", SqlColumnKind.Bool),
            new SqlColumn("InheritDisableCursorBlinking", SqlColumnKind.Bool),
            new SqlColumn("InheritDisableCursorShadow", SqlColumnKind.Bool),
            new SqlColumn("InheritDisableFullWindowDrag", SqlColumnKind.Bool),
            new SqlColumn("InheritDisableMenuAnimations", SqlColumnKind.Bool),
            new SqlColumn("InheritDisplayThemes", SqlColumnKind.Bool),
            new SqlColumn("InheritDisplayWallpaper", SqlColumnKind.Bool),
            new SqlColumn("InheritDomain", SqlColumnKind.Bool),
            new SqlColumn("InheritEnableDesktopComposition", SqlColumnKind.Bool),
            new SqlColumn("InheritEnableFontSmoothing", SqlColumnKind.Bool),
            new SqlColumn("InheritExtApp", SqlColumnKind.Bool),
            new SqlColumn("InheritFavorite", SqlColumnKind.Bool),
            new SqlColumn("InheritICAEncryptionStrength", SqlColumnKind.Bool),
            new SqlColumn("InheritIcon", SqlColumnKind.Bool),
            new SqlColumn("InheritLoadBalanceInfo", SqlColumnKind.Bool),
            new SqlColumn("InheritMacAddress", SqlColumnKind.Bool),
            new SqlColumn("InheritOpeningCommand", SqlColumnKind.Bool),
            new SqlColumn("InheritPanel", SqlColumnKind.Bool),
            new SqlColumn("InheritPassword", SqlColumnKind.Bool),
            new SqlColumn("InheritPort", SqlColumnKind.Bool),
            new SqlColumn("InheritPostExtApp", SqlColumnKind.Bool),
            new SqlColumn("InheritPreExtApp", SqlColumnKind.Bool),
            new SqlColumn("InheritProtocol", SqlColumnKind.Bool),
            new SqlColumn("InheritPuttySession", SqlColumnKind.Bool),
            new SqlColumn("InheritRDGatewayDomain", SqlColumnKind.Bool),
            new SqlColumn("InheritRDGatewayHostname", SqlColumnKind.Bool),
            new SqlColumn("InheritRDGatewayPassword", SqlColumnKind.Bool),
            new SqlColumn("InheritRDGatewayUsageMethod", SqlColumnKind.Bool),
            new SqlColumn("InheritRDGatewayUseConnectionCredentials", SqlColumnKind.Bool),
            new SqlColumn("InheritRDGatewayExternalCredentialProvider", SqlColumnKind.Bool),
            new SqlColumn("InheritRDGatewayUsername", SqlColumnKind.Bool),
            new SqlColumn("InheritRDGatewayUserViaAPI", SqlColumnKind.Bool),
            new SqlColumn("InheritRDPAlertIdleTimeout", SqlColumnKind.Bool),
            new SqlColumn("InheritRDPAuthenticationLevel", SqlColumnKind.Bool),
            new SqlColumn("InheritRDPMinutesToIdleTimeout", SqlColumnKind.Bool),
            new SqlColumn("InheritRdpVersion", SqlColumnKind.Bool),
            new SqlColumn("InheritRedirectAudioCapture", SqlColumnKind.Bool),
            new SqlColumn("InheritRedirectClipboard", SqlColumnKind.Bool),
            new SqlColumn("InheritRedirectDiskDrives", SqlColumnKind.Bool),
            new SqlColumn("InheritRedirectDiskDrivesCustom", SqlColumnKind.Bool),
            new SqlColumn("InheritRedirectKeys", SqlColumnKind.Bool),
            new SqlColumn("InheritRedirectPorts", SqlColumnKind.Bool),
            new SqlColumn("InheritRedirectPrinters", SqlColumnKind.Bool),
            new SqlColumn("InheritRedirectSmartCards", SqlColumnKind.Bool),
            new SqlColumn("InheritRedirectSound", SqlColumnKind.Bool),
            new SqlColumn("InheritRenderingEngine", SqlColumnKind.Bool),
            new SqlColumn("InheritResolution", SqlColumnKind.Bool),
            new SqlColumn("InheritSSHOptions", SqlColumnKind.Bool),
            new SqlColumn("InheritSSHTunnelConnectionName", SqlColumnKind.Bool),
            new SqlColumn("InheritSoundQuality", SqlColumnKind.Bool),
            new SqlColumn("InheritUseConsoleSession", SqlColumnKind.Bool),
            new SqlColumn("InheritUseCredSsp", SqlColumnKind.Bool),
            new SqlColumn("InheritUseRestrictedAdmin", SqlColumnKind.Bool),
            new SqlColumn("InheritUseRCG", SqlColumnKind.Bool),
            new SqlColumn("InheritExternalCredentialProvider", SqlColumnKind.Bool),
            new SqlColumn("InheritUserViaAPI", SqlColumnKind.Bool),
            new SqlColumn("UseRestrictedAdmin", SqlColumnKind.Bool),
            new SqlColumn("UseRCG", SqlColumnKind.Bool),
            new SqlColumn("InheritUseEnhancedMode", SqlColumnKind.Bool, Nullable: true),
            new SqlColumn("InheritUseVmId", SqlColumnKind.Bool, Nullable: true),
            new SqlColumn("InheritUserField", SqlColumnKind.Bool),
            new SqlColumn("InheritUsername", SqlColumnKind.Bool),
            new SqlColumn("InheritVNCAuthMode", SqlColumnKind.Bool),
            new SqlColumn("InheritVNCColors", SqlColumnKind.Bool),
            new SqlColumn("InheritVNCCompression", SqlColumnKind.Bool),
            new SqlColumn("InheritVNCEncoding", SqlColumnKind.Bool),
            new SqlColumn("InheritVNCProxyIP", SqlColumnKind.Bool),
            new SqlColumn("InheritVNCProxyPassword", SqlColumnKind.Bool),
            new SqlColumn("InheritVNCProxyPort", SqlColumnKind.Bool),
            new SqlColumn("InheritVNCProxyType", SqlColumnKind.Bool),
            new SqlColumn("InheritVNCProxyUsername", SqlColumnKind.Bool),
            new SqlColumn("InheritVNCSmartSizeMode", SqlColumnKind.Bool),
            new SqlColumn("InheritVNCViewOnly", SqlColumnKind.Bool),
            new SqlColumn("InheritVmId", SqlColumnKind.Bool),
            new SqlColumn("StartProgram", SqlColumnKind.String, Size: 512, Nullable: true),
            new SqlColumn("StartProgramWorkDir", SqlColumnKind.String, Size: 512, Nullable: true),
            new SqlColumn("EC2Region", SqlColumnKind.String, Size: 32, Nullable: true),
            new SqlColumn("EC2InstanceId", SqlColumnKind.String, Size: 32, Nullable: true),
            new SqlColumn("ExternalCredentialProvider", SqlColumnKind.String, Size: 256, Nullable: true),
            new SqlColumn("ExternalAddressProvider", SqlColumnKind.String, Size: 256, Nullable: true),
            new SqlColumn("UserViaAPI", SqlColumnKind.String, Size: 512),
            // Written by the legacy DataTableSerializer but missing from its CREATE TABLE script; created here
            // so legacy clients can save to a database initialised by this version.
            new SqlColumn("EnvironmentTags", SqlColumnKind.String, Size: 1024, Nullable: true, IsExtension: true),
            new SqlColumn("InheritEnvironmentTags", SqlColumnKind.Bool, Nullable: true, IsExtension: true),
            new SqlColumn("RDGatewayExternalCredentialProvider", SqlColumnKind.String, Size: 256, Nullable: true, IsExtension: true),
            new SqlColumn("RDGatewayUserViaAPI", SqlColumnKind.String, Size: 512, Nullable: true, IsExtension: true),
            new SqlColumn("EnhancedMode", SqlColumnKind.Bool, Nullable: true, IsExtension: true),
            new SqlColumn("InheritEnhancedMode", SqlColumnKind.Bool, Nullable: true, IsExtension: true),
        ];

        private static readonly Dictionary<string, SqlColumn> ByName =
            ConnectionColumns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        public static SqlColumn? FindColumn(string name) => ByName.GetValueOrDefault(name);
    }
}
