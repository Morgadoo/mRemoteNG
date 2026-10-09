using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace mRemoteNG.Core.Settings;

/// <summary>The kind of machine an update is for.</summary>
public sealed record UpdatePlatform(OSPlatform Os, Architecture Architecture)
{
    public static UpdatePlatform Current { get; } = new(
        OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux,
        RuntimeInformation.OSArchitecture);
}

/// <summary>Proxy used for the update check and download (Options ▸ Updates, legacy <c>UpdateUseProxy</c> …).</summary>
public sealed record UpdateProxySettings(
    bool UseProxy,
    string Address,
    int Port,
    bool UseAuthentication = false,
    string Username = "",
    string Password = "")
{
    public static UpdateProxySettings None { get; } = new(false, string.Empty, 0);

    /// <summary>The proxy URI ("http://host:port"), or null when no custom proxy is configured.</summary>
    public Uri? ProxyUri
    {
        get
        {
            if (!UseProxy || string.IsNullOrWhiteSpace(Address))
                return null;
            var address = Address.Contains("://", StringComparison.Ordinal) ? Address.Trim() : "http://" + Address.Trim();
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
                return null;
            return Port is > 0 and <= 65535 ? new UriBuilder(uri) { Port = Port }.Uri : uri;
        }
    }

    /// <summary>
    /// An HTTP handler honouring these settings: the configured proxy (with credentials) or, when none,
    /// the system proxy (environment variables / OS settings).
    /// </summary>
    public HttpMessageHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };

        if (ProxyUri is { } uri)
        {
            var proxy = new WebProxy(uri);
            if (UseAuthentication)
                proxy.Credentials = new NetworkCredential(Username, Password);
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }

        return handler;
    }
}

/// <summary>Result of <see cref="UpdateDownloader.DownloadAsync"/>.</summary>
/// <param name="Verified">True when a published SHA-256 matched; false when none was published.</param>
public sealed record UpdateDownloadResult(bool Succeeded, string? FilePath, ReleaseAsset? Asset, bool Verified, string Message);

/// <summary>
/// Picks the release asset for this platform and downloads it: AppImage (or .deb / .tar.gz) on Linux,
/// .msi (or .zip) on Windows, .dmg (or .zip) on macOS, matching the CPU architecture. The download is
/// checked against the size GitHub publishes and, when available, a SHA-256 (GitHub's asset digest or a
/// checksum file in the release such as <c>SHA256SUMS</c> or <c>&lt;asset&gt;.sha256</c>).
/// A file that fails verification is deleted.
/// </summary>
public sealed partial class UpdateDownloader
{
    private readonly HttpClient _http;

    public UpdateDownloader(HttpClient httpClient)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>The best asset for <paramref name="platform"/>, or null when the release has none.</summary>
    public static ReleaseAsset? SelectAsset(IReadOnlyList<ReleaseAsset> assets, UpdatePlatform platform)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(platform);

        return assets
            .Select(a => (Asset: a, Score: Score(a.Name, platform)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Asset.Name.Length)
            .Select(x => x.Asset)
            .FirstOrDefault();
    }

    /// <summary>0 = unusable on this platform; higher is better.</summary>
    public static int Score(string assetName, UpdatePlatform platform)
    {
        var name = assetName.ToLowerInvariant();
        if (IsChecksumFile(name) || name.EndsWith(".sig", StringComparison.Ordinal) || name.EndsWith(".asc", StringComparison.Ordinal)
            || name.Contains("symbols", StringComparison.Ordinal) || name.Contains("pdb", StringComparison.Ordinal))
            return 0;

        int formatScore;
        if (platform.Os == OSPlatform.Linux)
        {
            formatScore = name.EndsWith(".appimage", StringComparison.Ordinal) ? 40
                : name.EndsWith(".deb", StringComparison.Ordinal) ? 30
                : name.EndsWith(".rpm", StringComparison.Ordinal) ? 25
                : name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tgz", StringComparison.Ordinal) ? 20
                : name.EndsWith(".zip", StringComparison.Ordinal) && MentionsOs(name, "linux") ? 15
                : 0;
            if (formatScore > 0 && (MentionsOs(name, "win") || MentionsOs(name, "osx") || MentionsOs(name, "mac")))
                return 0;
        }
        else if (platform.Os == OSPlatform.Windows)
        {
            formatScore = name.EndsWith(".msi", StringComparison.Ordinal) ? 40
                : name.EndsWith(".exe", StringComparison.Ordinal) && name.Contains("setup", StringComparison.Ordinal) ? 35
                : name.EndsWith(".zip", StringComparison.Ordinal) ? 20
                : 0;
            if (formatScore > 0 && (MentionsOs(name, "linux") || MentionsOs(name, "osx") || MentionsOs(name, "mac")))
                return 0;
        }
        else
        {
            formatScore = name.EndsWith(".dmg", StringComparison.Ordinal) ? 40
                : name.EndsWith(".pkg", StringComparison.Ordinal) ? 35
                : name.EndsWith(".zip", StringComparison.Ordinal) && (MentionsOs(name, "osx") || MentionsOs(name, "mac")) ? 20
                : 0;
            if (formatScore > 0 && (MentionsOs(name, "linux") || MentionsOs(name, "win")))
                return 0;
        }

        if (formatScore == 0)
            return 0;

        var mentionsArm = ArmPattern().IsMatch(name);
        var mentionsX64 = X64Pattern().IsMatch(name);
        var mentionsX86 = X86Pattern().IsMatch(name);
        var isArm = platform.Architecture is Architecture.Arm64 or Architecture.Arm;

        if (isArm)
        {
            if (mentionsArm) return formatScore + 10;
            if (mentionsX64 || mentionsX86) return 0;
        }
        else
        {
            if (mentionsArm) return 0;
            if (mentionsX64) return formatScore + 10;
            if (mentionsX86) return platform.Architecture == Architecture.X86 ? formatScore + 10 : 0;
        }

        // No architecture in the name: usable, but a matching one is preferred.
        return formatScore;
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> into <paramref name="targetDirectory"/> and verifies it.
    /// <paramref name="allAssets"/> is searched for checksum files when the asset carries no digest.
    /// </summary>
    public async Task<UpdateDownloadResult> DownloadAsync(
        ReleaseAsset asset,
        IReadOnlyList<ReleaseAsset> allAssets,
        string targetDirectory,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        var fileName = Path.GetFileName(asset.Name);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new UpdateDownloadResult(false, null, asset, false, $"Invalid asset name '{asset.Name}'.");

        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, fileName);
        var partial = target + ".part";

        try
        {
            var expectedHash = asset.Sha256 ?? await FindPublishedHashAsync(asset, allAssets ?? [], ct).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("mRemoteNG", "update"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new UpdateDownloadResult(false, null, asset, false, $"Download failed: {(int)response.StatusCode} {response.ReasonPhrase}.");

            var total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : (long?)null);
            long written = 0;
            using var sha = SHA256.Create();
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    written += read;
                    if (total is > 0)
                        progress?.Report(Math.Min(1.0, (double)written / total.Value));
                }
            }

            sha.TransformFinalBlock([], 0, 0);
            var actualHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

            if (asset.Size > 0 && written != asset.Size)
            {
                File.Delete(partial);
                return new UpdateDownloadResult(false, null, asset, false,
                    $"The download is {written} bytes but GitHub lists {asset.Size}; it was deleted.");
            }

            if (expectedHash is not null && !string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                return new UpdateDownloadResult(false, null, asset, false,
                    $"SHA-256 mismatch (expected {expectedHash}, got {actualHash}); the download was deleted.");
            }

            File.Move(partial, target, overwrite: true);
            if (!OperatingSystem.IsWindows() && target.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase))
            {
                File.SetUnixFileMode(target, File.GetUnixFileMode(target)
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            progress?.Report(1.0);
            var verified = expectedHash is not null;
            var message = verified
                ? $"Downloaded {fileName} ({written:N0} bytes, SHA-256 verified)."
                : $"Downloaded {fileName} ({written:N0} bytes; no checksum was published to verify it).";
            return new UpdateDownloadResult(true, target, asset, verified, message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(partial);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            TryDelete(partial);
            return new UpdateDownloadResult(false, null, asset, false, $"Download failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Looks for the SHA-256 of <paramref name="asset"/> in "&lt;asset&gt;.sha256" or a combined checksum file
    /// (SHA256SUMS, checksums.txt, *.sha256sum) in the same release. Null when none lists it.
    /// </summary>
    public async Task<string?> FindPublishedHashAsync(ReleaseAsset asset, IReadOnlyList<ReleaseAsset> allAssets, CancellationToken ct = default)
    {
        var candidates = allAssets
            .Where(a => IsChecksumFile(a.Name.ToLowerInvariant()))
            .OrderByDescending(a => a.Name.StartsWith(asset.Name, StringComparison.OrdinalIgnoreCase))
            .Take(4);

        foreach (var file in candidates)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("mRemoteNG", "update"));
                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var hash = ParseChecksumFile(text, asset.Name, singleFile: file.Name.StartsWith(asset.Name, StringComparison.OrdinalIgnoreCase));
                if (hash is not null)
                    return hash;
            }
            catch (HttpRequestException)
            {
                // A missing checksum file only means the download cannot be verified.
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the hash for <paramref name="assetName"/> from "hash  name" / "hash *name" lines or
    /// "SHA256 (name) = hash". A file for a single asset may contain just the hash.
    /// </summary>
    public static string? ParseChecksumFile(string text, string assetName, bool singleFile)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var bsd = BsdChecksumPattern().Match(line);
            if (bsd.Success)
            {
                if (string.Equals(bsd.Groups["name"].Value, assetName, StringComparison.OrdinalIgnoreCase))
                    return bsd.Groups["hash"].Value.ToLowerInvariant();
                continue;
            }

            var gnu = GnuChecksumPattern().Match(line);
            if (gnu.Success)
            {
                var name = gnu.Groups["name"].Value.Trim();
                if (name.Length == 0 ? singleFile : string.Equals(Path.GetFileName(name), assetName, StringComparison.OrdinalIgnoreCase))
                    return gnu.Groups["hash"].Value.ToLowerInvariant();
            }
        }

        return null;
    }

    /// <summary>A good place to put downloads: the user's Downloads folder, else the temp folder.</summary>
    public static string DefaultDownloadDirectory()
    {
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads : Path.Combine(Path.GetTempPath(), "mRemoteNG-updates");
    }

    private static bool IsChecksumFile(string lowerName) =>
        lowerName.EndsWith(".sha256", StringComparison.Ordinal) || lowerName.EndsWith(".sha256sum", StringComparison.Ordinal)
        || lowerName.Contains("sha256sums", StringComparison.Ordinal) || lowerName.Contains("checksums", StringComparison.Ordinal);

    private static bool MentionsOs(string lowerName, string token) =>
        Regex.IsMatch(lowerName, $@"(^|[^a-z]){token}", RegexOptions.CultureInvariant);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex(@"(^|[^a-z0-9])(arm64|aarch64|arm)([^a-z0-9]|$)")]
    private static partial Regex ArmPattern();

    [GeneratedRegex(@"(^|[^a-z0-9])(x64|amd64|x86_64|x86-64)([^a-z0-9]|$)")]
    private static partial Regex X64Pattern();

    [GeneratedRegex(@"(^|[^a-z0-9])(x86|i386|i686|win32)([^a-z0-9]|$)")]
    private static partial Regex X86Pattern();

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})(\s+\*?(?<name>.*))?$")]
    private static partial Regex GnuChecksumPattern();

    [GeneratedRegex(@"^SHA256\s*\((?<name>[^)]+)\)\s*=\s*(?<hash>[0-9a-fA-F]{64})$", RegexOptions.IgnoreCase)]
    private static partial Regex BsdChecksumPattern();
}
