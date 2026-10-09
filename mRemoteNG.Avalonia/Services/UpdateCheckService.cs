using System.Reflection;
using mRemoteNG.Core.Settings;
using mRemoteNG.Platform.Security;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Checks GitHub (mRemoteNG/mRemoteNG releases) for a newer version and downloads the package for this
/// platform. Used by the Options "Updates" page and by the optional startup check. Requests go through the
/// proxy configured in Options ▸ Updates (otherwise the system proxy).
/// </summary>
public sealed class UpdateCheckService : IDisposable
{
    private static readonly TimeSpan MinimumStartupInterval = TimeSpan.FromHours(24);

    private readonly AppSettingsService _settings;
    private readonly ICryptoProvider? _crypto;
    private readonly string _releasesUrl;
    private readonly object _sync = new();
    private HttpClient? _http;
    private UpdateProxySettings? _proxyInUse;

    public UpdateCheckService(AppSettingsService settings, ICryptoProvider? crypto = null)
        : this(settings, crypto, UpdateChecker.DefaultReleasesUrl)
    {
    }

    /// <summary>Test seam: <paramref name="releasesUrl"/> replaces the GitHub API URL.</summary>
    public UpdateCheckService(AppSettingsService settings, ICryptoProvider? crypto, string releasesUrl)
    {
        _settings = settings;
        _crypto = crypto;
        _releasesUrl = releasesUrl;
        var informational = typeof(UpdateCheckService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        CurrentVersionText = informational?.Split('+')[0] ?? "unknown";
        CurrentVersion = UpdateChecker.ParseVersion(CurrentVersionText)
            ?? typeof(UpdateCheckService).Assembly.GetName().Version
            ?? new Version(0, 0);
    }

    public Version CurrentVersion { get; }

    public string CurrentVersionText { get; }

    /// <summary>The platform whose package is downloaded.</summary>
    public UpdatePlatform Platform { get; set; } = UpdatePlatform.Current;

    /// <summary>Where downloads go.</summary>
    public string DownloadDirectory { get; set; } = UpdateDownloader.DefaultDownloadDirectory();

    /// <summary>The proxy from <paramref name="settings"/> (password decrypted).</summary>
    public UpdateProxySettings GetProxySettings(AppSettings settings)
    {
        if (!settings.UpdateUseProxy)
            return UpdateProxySettings.None;

        var password = string.Empty;
        if (!string.IsNullOrEmpty(settings.UpdateProxyPasswordProtected) && _crypto is not null)
        {
            try
            {
                password = _crypto.Unprotect(settings.UpdateProxyPasswordProtected);
            }
            catch (Exception)
            {
                password = string.Empty;
            }
        }

        return new UpdateProxySettings(true, settings.UpdateProxyAddress, settings.UpdateProxyPort,
            settings.UpdateProxyUseAuthentication, settings.UpdateProxyUsername, password);
    }

    public Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken ct = default) =>
        CheckAsync(channel, null, ct);

    /// <summary>Checks with <paramref name="proxyFrom"/>'s proxy (e.g. unsaved Options values), or the saved settings.</summary>
    public Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, AppSettings? proxyFrom, CancellationToken ct = default) =>
        new UpdateChecker(GetClient(proxyFrom), _releasesUrl).CheckAsync(CurrentVersion, channel, ct);

    /// <summary>Downloads the package for <see cref="Platform"/> from a successful check.</summary>
    public async Task<UpdateDownloadResult> DownloadAsync(UpdateCheckResult check, IProgress<double>? progress = null,
        AppSettings? proxyFrom = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        var asset = UpdateDownloader.SelectAsset(check.Assets, Platform);
        if (asset is null)
        {
            return new UpdateDownloadResult(false, null, null, false,
                $"Release {check.LatestTag} has no package for {Platform.Os} {Platform.Architecture}. Download it from {check.ReleaseUrl}.");
        }

        return await new UpdateDownloader(GetClient(proxyFrom))
            .DownloadAsync(asset, check.Assets, DownloadDirectory, progress, ct)
            .ConfigureAwait(false);
    }

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

    private HttpClient GetClient(AppSettings? proxyFrom)
    {
        var proxy = GetProxySettings(proxyFrom ?? _settings.Current);
        lock (_sync)
        {
            if (_http is not null && proxy == _proxyInUse)
                return _http;

            // Not disposed: a request may still be using the old client; it is collected afterwards.
            _http = new HttpClient(proxy.CreateHandler()) { Timeout = TimeSpan.FromMinutes(10) };
            _proxyInUse = proxy;
            return _http;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _http?.Dispose();
            _http = null;
        }
    }
}
