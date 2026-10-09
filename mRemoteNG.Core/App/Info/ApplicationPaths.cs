using System.Runtime.InteropServices;
using mRemoteNG.Platform.Settings;

namespace mRemoteNG.Core.App.Info;

/// <summary>
/// Cross-platform filesystem paths used by mRemoteNG configuration components.
/// In portable mode (<see cref="AppDataLocation"/>) every data path points next to the executable.
/// </summary>
public static class ApplicationPaths
{
    public const string ProductName = "mRemoteNG";

    /// <summary>Default connection file name (same as the legacy app).</summary>
    public const string DefaultConnectionsFileName = "confCons.xml";

    /// <summary>Log file name (same as the legacy app's log4net file).</summary>
    public const string LogFileName = "mRemoteNG.log";

    /// <summary>Folder (inside <see cref="SettingsDirectory"/>) holding user themes.</summary>
    public const string ThemesFolderName = "Themes";

    public static string ExecutableDirectory => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>True when data is kept next to the executable.</summary>
    public static bool IsPortable => AppDataLocation.IsPortable;

    public static string SettingsDirectory => AppDataLocation.Resolve(PerUserSettingsDirectory);

    /// <summary>The per-user data directory (ignores portable mode).</summary>
    public static string PerUserSettingsDirectory()
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
        if (!string.IsNullOrWhiteSpace(xdgConfig) && Path.IsPathRooted(xdgConfig))
        {
            return Path.Combine(xdgConfig, ProductName);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config", ProductName);
    }

    /// <summary>The default connection file in the data directory.</summary>
    public static string DefaultConnectionsFilePath => Path.Combine(SettingsDirectory, DefaultConnectionsFileName);

    /// <summary>The default log file in the data directory.</summary>
    public static string DefaultLogFilePath => Path.Combine(SettingsDirectory, LogFileName);

    /// <summary>The folder with user-defined themes.</summary>
    public static string UserThemesDirectory => Path.Combine(SettingsDirectory, ThemesFolderName);

    public static string PortableSettingsFilePath(string applicationName)
        => Path.Combine(SettingsDirectory, $"{applicationName}.settings");

    /// <summary>
    /// Stores <paramref name="path"/> relative to the portable directory when it lies inside it,
    /// so a portable installation keeps working after being moved. Otherwise returns the full path.
    /// </summary>
    public static string ToStoredPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!IsPortable)
            return full;

        var relative = Path.GetRelativePath(SettingsDirectory, full);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative;
    }

    /// <summary>Resolves a path stored by <see cref="ToStoredPath"/> (relative paths are relative to the data directory).</summary>
    public static string FromStoredPath(string storedPath)
    {
        var expanded = Environment.ExpandEnvironmentVariables(storedPath);
        return Path.IsPathRooted(expanded) ? expanded : Path.GetFullPath(Path.Combine(SettingsDirectory, expanded));
    }
}
