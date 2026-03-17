using System.Runtime.InteropServices;

namespace mRemoteNG.Core.App.Info;

/// <summary>
/// Cross-platform filesystem paths used by mRemoteNG configuration components.
/// </summary>
public static class ApplicationPaths
{
    public const string ProductName = "mRemoteNG";

    public static string ExecutableDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string SettingsDirectory
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ProductName);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Application Support", ProductName);
            }

            var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdgConfig))
            {
                return Path.Combine(xdgConfig, ProductName);
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config", ProductName);
        }
    }

    public static string PortableSettingsFilePath(string applicationName)
        => Path.Combine(SettingsDirectory, $"{applicationName}.settings");
}
