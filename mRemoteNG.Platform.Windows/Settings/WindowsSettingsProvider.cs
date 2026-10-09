using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using mRemoteNG.Platform.Settings;

namespace mRemoteNG.Platform.Windows.Settings;

/// <summary>
/// Windows settings provider: stores settings in %APPDATA%\mRemoteNG\settings.xml
/// (XML, not the registry), so the same file format is used on every platform.
/// The legacy WinForms app imports old registry values through its own SettingsMigrationHelper.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSettingsProvider : XmlFileSettingsProvider
{
    public WindowsSettingsProvider(ILogger<WindowsSettingsProvider>? logger = null)
        : base(AppDataLocation.Resolve(ResolveApplicationDataDirectory), logger)
    {
    }

    public static string ResolveApplicationDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mRemoteNG");
}
