using System.Reflection;
using mRemoteNG.Core.Settings;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Checks GitHub (mRemoteNG/mRemoteNG releases) for a newer version.
/// Used by the Options "Updates" page and by the optional startup check.
/// </summary>
public sealed class UpdateCheckService
{
    private static readonly TimeSpan MinimumStartupInterval = TimeSpan.FromHours(24);

    private readonly AppSettingsService _settings;
    private readonly UpdateChecker _checker;

    public UpdateCheckService(AppSettingsService settings)
        : this(settings, new UpdateChecker(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }))
    {
    }

    private UpdateCheckService(AppSettingsService settings, UpdateChecker checker)
    {
        _settings = settings;
        _checker = checker;
        var informational = typeof(UpdateCheckService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        CurrentVersionText = informational?.Split('+')[0] ?? "unknown";
        CurrentVersion = UpdateChecker.ParseVersion(CurrentVersionText)
            ?? typeof(UpdateCheckService).Assembly.GetName().Version
            ?? new Version(0, 0);
    }

    public Version CurrentVersion { get; }

    public string CurrentVersionText { get; }

    public Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken ct = default) =>
        _checker.CheckAsync(CurrentVersion, channel, ct);

    /// <summary>
    /// Runs the automatic check when enabled and the last one is older than a day.
    /// Returns null when no check was due.
    /// </summary>
    public async Task<UpdateCheckResult?> RunStartupCheckAsync(CancellationToken ct = default)
    {
        var settings = _settings.Current;
        if (!settings.CheckForUpdatesOnStartup)
            return null;
        if (settings.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < MinimumStartupInterval)
            return null;

        var result = await CheckAsync(settings.UpdateChannel, ct).ConfigureAwait(false);
        if (result.Succeeded)
            _settings.Update(s => s.LastUpdateCheckUtc = DateTime.UtcNow);
        return result;
    }
}
