using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace mRemoteNG.Core.Settings;

/// <summary>A downloadable file attached to a GitHub release.</summary>
/// <param name="Size">Size in bytes as published by GitHub (0 when unknown).</param>
/// <param name="Sha256">Lower-case hex SHA-256 from GitHub's asset digest or a checksum file, when published.</param>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256 = null);

/// <summary>Outcome of an update check.</summary>
public sealed record UpdateCheckResult(
    bool Succeeded,
    bool IsUpdateAvailable,
    Version? LatestVersion,
    string? LatestTag,
    string? ReleaseUrl,
    string Message)
{
    /// <summary>The files attached to the newest release (empty when the check failed).</summary>
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];
}

/// <summary>
/// Queries the GitHub releases API of mRemoteNG/mRemoteNG and compares the newest release
/// on the selected channel with the running version.
/// </summary>
public sealed partial class UpdateChecker
{
    public const string DefaultReleasesUrl = "https://api.github.com/repos/mRemoteNG/mRemoteNG/releases?per_page=30";

    private readonly HttpClient _http;
    private readonly string _releasesUrl;

    public UpdateChecker(HttpClient httpClient, string releasesUrl = DefaultReleasesUrl)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _releasesUrl = releasesUrl;
    }

    /// <summary>Extracts "major.minor[.build[.revision]]" from a tag such as "v1.77.3", "1.76.20" or "20240627-v1.77.0-NB".</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = VersionPattern().Match(text);
        return match.Success && Version.TryParse(match.Value, out var version) ? version : null;
    }

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, UpdateChannel channel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _releasesUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("mRemoteNG", currentVersion.ToString()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Failed($"GitHub returned {(int)response.StatusCode} {response.ReasonPhrase}.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Failed("Unexpected response from GitHub.");

            (Version Version, string Tag, string? Url, JsonElement Release)? latest = null;
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (GetBool(release, "draft"))
                    continue;
                if (channel == UpdateChannel.Stable && GetBool(release, "prerelease"))
                    continue;

                var tag = GetString(release, "tag_name") ?? string.Empty;
                var version = ParseVersion(tag) ?? ParseVersion(GetString(release, "name"));
                if (version is null)
                    continue;

                if (latest is null || version > latest.Value.Version)
                    latest = (version, tag, GetString(release, "html_url"), release.Clone());
            }

            if (latest is null)
                return Failed("No release found on the selected channel.");

            var (latestVersion, latestTag, url, latestRelease) = latest.Value;
            var newer = Normalize(latestVersion) > Normalize(currentVersion);
            var message = newer
                ? $"mRemoteNG {latestTag} is available (you have {currentVersion})."
                : $"You are running the latest version ({currentVersion}; newest release is {latestTag}).";
            return new UpdateCheckResult(true, newer, latestVersion, latestTag, url, message)
            {
                Assets = ReadAssets(latestRelease),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed("The update check timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return Failed($"Could not reach GitHub: {ex.Message}");
        }
    }

    /// <summary>Reads a release's "assets" array; GitHub's "digest" ("sha256:...") becomes <see cref="ReleaseAsset.Sha256"/>.</summary>
    public static IReadOnlyList<ReleaseAsset> ReadAssets(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<ReleaseAsset>();
        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            var url = GetString(asset, "browser_download_url");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                continue;

            var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var bytes) ? bytes : 0;
            var digest = GetString(asset, "digest");
            var sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? digest["sha256:".Length..].Trim().ToLowerInvariant()
                : null;
            list.Add(new ReleaseAsset(name, url, size, sha256));
        }

        return list;
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static UpdateCheckResult Failed(string message) =>
        new(false, false, null, null, null, message);

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    [GeneratedRegex(@"\d+\.\d+(\.\d+){0,2}")]
    private static partial Regex VersionPattern();
}
