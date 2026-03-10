using System;
using System.IO;
using System.Runtime.Versioning;
using mRemoteNG.Connection;
using mRemoteNG.Core.App.Info;

namespace mRemoteNG.App.Info
{
    [SupportedOSPlatform("windows")]
    public static class SettingsFileInfo
    {
        private static readonly string ExePath = Path.GetDirectoryName(typeof(ConnectionInfo).Assembly.Location);

        public static string SettingsPath => Runtime.IsPortableEdition
            ? ExePath ?? ApplicationPaths.ExecutableDirectory
            : ApplicationPaths.SettingsDirectory;

        public static string LayoutFileName { get; } = "pnlLayout.xml";
        public static string ExtAppsFilesName { get; } = "extApps.xml";
        public static string ThemesFileName { get; } = "Themes.xml";
        public static string LocalConnectionProperties { get; } = "LocalConnectionProperties.xml";

        public static string ThemeFolder { get; } =
            SettingsPath != null ? Path.Combine(SettingsPath, "Themes") : String.Empty;

        public static string InstalledThemeFolder { get; } =
            ExePath != null ? Path.Combine(ExePath, "Themes") : String.Empty;
    }
}
