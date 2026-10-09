using mRemoteNG.Core.App.Info;

namespace mRemoteNG.Core.Settings;

/// <summary>
/// Startup/exit decisions driven by <see cref="AppSettings"/>.
/// </summary>
public sealed class StartupService
{
    private readonly AppSettingsService _settings;
    private readonly Func<string, bool> _fileExists;

    public StartupService(AppSettingsService settings)
        : this(settings, File.Exists)
    {
    }

    /// <summary>Test seam: <paramref name="fileExists"/> replaces <see cref="File.Exists(string)"/>.</summary>
    public StartupService(AppSettingsService settings, Func<string, bool> fileExists)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _fileExists = fileExists ?? throw new ArgumentNullException(nameof(fileExists));
    }

    /// <summary>
    /// The connection file to load when the app starts, or null to start empty
    /// (behaviour is <see cref="StartupFileBehavior.None"/>, the configured file no longer exists, or the
    /// connections are loaded from a SQL database).
    /// </summary>
    public string? GetFileToOpenAtStartup()
    {
        var settings = _settings.Current;
        // With "use SQL server" the connections come from the database (legacy behaviour).
        if (settings.UseSqlServer)
            return null;

        var path = settings.StartupBehavior switch
        {
            StartupFileBehavior.ReopenLastFile => settings.LastConnectionFilePath,
            StartupFileBehavior.OpenSpecificFile => settings.StartupFilePath,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(path))
        {
            // A portable installation opens the confCons.xml that travels with it, like the legacy app.
            if (settings.StartupBehavior != StartupFileBehavior.None && ApplicationPaths.IsPortable
                && _fileExists(ApplicationPaths.DefaultConnectionsFilePath))
            {
                return ApplicationPaths.DefaultConnectionsFilePath;
            }

            return null;
        }

        path = ApplicationPaths.FromStoredPath(path);
        return _fileExists(path) ? path : null;
    }

    /// <summary>
    /// Remembers the connection file that is open now, so it can be reopened next time.
    /// A null/empty path (no file open) keeps the previously remembered file.
    /// </summary>
    public void RecordOpenFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        // Portable installations store paths inside the portable folder relative to it.
        var value = ApplicationPaths.ToStoredPath(path);
        if (_settings.Current.LastConnectionFilePath == value)
            return;

        _settings.Update(s => s.LastConnectionFilePath = value);
    }
}
