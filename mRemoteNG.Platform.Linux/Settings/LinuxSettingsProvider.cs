using Microsoft.Extensions.Logging;
using mRemoteNG.Platform.Settings;

namespace mRemoteNG.Platform.Linux.Settings;

/// <summary>
/// Linux settings provider following the XDG Base Directory Specification.
/// Stores settings in: $XDG_CONFIG_HOME/mRemoteNG/settings.xml
/// Defaults to: ~/.config/mRemoteNG/settings.xml
/// </summary>
public sealed class LinuxSettingsProvider : XmlFileSettingsProvider
{
    public LinuxSettingsProvider(ILogger<LinuxSettingsProvider>? logger = null)
        : base(AppDataLocation.Resolve(ResolveApplicationDataDirectory), logger)
    {
    }

    /// <summary>
    /// Resolves the configuration directory. Per the XDG spec, an empty or relative
    /// <c>XDG_CONFIG_HOME</c> is ignored and <c>~/.config</c> is used instead.
    /// </summary>
    public static string ResolveApplicationDataDirectory()
    {
        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(xdgConfig) || !Path.IsPathRooted(xdgConfig))
            xdgConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return Path.Combine(xdgConfig, "mRemoteNG");
    }
}
