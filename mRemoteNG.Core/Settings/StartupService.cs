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
    /// (behaviour is <see cref="StartupFileBehavior.None"/>, or the configured file no longer exists).
    /// </summary>
    public string? GetFileToOpenAtStartup()
    {
        var settings = _settings.Current;
        var path = settings.StartupBehavior switch
        {
            StartupFileBehavior.ReopenLastFile => settings.LastConnectionFilePath,
            StartupFileBehavior.OpenSpecificFile => settings.StartupFilePath,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = Environment.ExpandEnvironmentVariables(path);
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

        var value = Path.GetFullPath(path);
        if (_settings.Current.LastConnectionFilePath == value)
            return;

        _settings.Update(s => s.LastConnectionFilePath = value);
    }
}
