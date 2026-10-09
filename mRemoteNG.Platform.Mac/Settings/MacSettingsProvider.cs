using Microsoft.Extensions.Logging;
using mRemoteNG.Platform.Settings;

namespace mRemoteNG.Platform.Mac.Settings;

/// <summary>
/// macOS settings provider following Apple's recommended storage conventions.
/// Stores settings in: ~/Library/Application Support/mRemoteNG/settings.xml
/// </summary>
public sealed class MacSettingsProvider : XmlFileSettingsProvider
{
    public MacSettingsProvider(ILogger<MacSettingsProvider>? logger = null)
        : base(ResolveApplicationDataDirectory(), logger)
    {
    }

    public static string ResolveApplicationDataDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "mRemoteNG");
}
