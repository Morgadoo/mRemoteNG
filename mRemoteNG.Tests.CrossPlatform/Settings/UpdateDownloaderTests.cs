using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using mRemoteNG.Core.Settings;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Settings;

/// <summary>
/// A minimal HTTP/1.1 server on 127.0.0.1 for update tests. Serves registered paths (also when the request
/// line carries an absolute URI, as a proxy receives it) and records each request's line and headers.
/// </summary>
internal sealed class FakeHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<string, (int Status, byte[] Body, string? ExtraHeaders)> _routes = new();
    private readonly CancellationTokenSource _cts = new();

    public FakeHttpServer()
    {
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public ConcurrentQueue<(string RequestLine, string Headers)> Requests { get; } = new();

    /// <summary>When set, requests without this Proxy-Authorization header get 407.</summary>
    public string? RequiredProxyAuthorization { get; set; }

    public void Map(string path, byte[] body, int status = 200, string? extraHeaders = null) => _routes[path] = (status, body, extraHeaders);

    public void Map(string path, string body, int status = 200) => Map(path, Encoding.UTF8.GetBytes(body), status);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (true)
            {
                var requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrEmpty(requestLine))
                    return;
                var headers = new StringBuilder();
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                    headers.AppendLine(line);
                Requests.Enqueue((requestLine, headers.ToString()));

                var target = requestLine.Split(' ')[1];
                var path = Uri.TryCreate(target, UriKind.Absolute, out var absolute) ? absolute.PathAndQuery : target;

                byte[] body;
                int status;
                var extra = string.Empty;
                if (RequiredProxyAuthorization is not null
                    && !headers.ToString().Contains($"Proxy-Authorization: {RequiredProxyAuthorization}", StringComparison.OrdinalIgnoreCase))
                {
                    status = 407;
                    body = [];
                    extra = "Proxy-Authenticate: Basic realm=\"test\"\r\n";
                }
                else if (_routes.TryGetValue(path, out var route))
                {
                    (status, body) = (route.Status, route.Body);
                    extra = route.ExtraHeaders ?? string.Empty;
                }
                else
                {
                    (status, body) = (404, Encoding.UTF8.GetBytes("not found"));
                }

                var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Length: {body.Length}\r\n{extra}Connection: keep-alive\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(body);
                await stream.FlushAsync();
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}

public sealed class UpdateDownloaderTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly FakeHttpServer _server = new();

    public void Dispose()
    {
        _server.Dispose();
        _dir.Dispose();
    }

    private static HttpClient DirectClient() => new(new SocketsHttpHandler { UseProxy = false });

    private static readonly ReleaseAsset[] TypicalAssets =
    [
        new("mRemoteNG-1.78.0-x64.msi", "u", 1),
        new("mRemoteNG-1.78.0-arm64.msi", "u", 1),
        new("mRemoteNG-1.78.0-portable-x64.zip", "u", 1),
        new("mRemoteNG-1.78.0-x86_64.AppImage", "u", 1),
        new("mRemoteNG-1.78.0-aarch64.AppImage", "u", 1),
        new("mremoteng_1.78.0_amd64.deb", "u", 1),
        new("mRemoteNG-1.78.0-osx-arm64.dmg", "u", 1),
        new("mRemoteNG-1.78.0-osx-x64.dmg", "u", 1),
        new("SHA256SUMS", "u", 1),
        new("mRemoteNG-1.78.0-x86_64.AppImage.sha256", "u", 1),
    ];

    [Theory]
    [InlineData("Windows", Architecture.X64, "mRemoteNG-1.78.0-x64.msi")]
    [InlineData("Windows", Architecture.Arm64, "mRemoteNG-1.78.0-arm64.msi")]
    [InlineData("Linux", Architecture.X64, "mRemoteNG-1.78.0-x86_64.AppImage")]
    [InlineData("Linux", Architecture.Arm64, "mRemoteNG-1.78.0-aarch64.AppImage")]
    [InlineData("OSX", Architecture.Arm64, "mRemoteNG-1.78.0-osx-arm64.dmg")]
    [InlineData("OSX", Architecture.X64, "mRemoteNG-1.78.0-osx-x64.dmg")]
    public void SelectAsset_PicksThePackageForThePlatform(string os, Architecture arch, string expected)
    {
        var platform = new UpdatePlatform(OSPlatform.Create(os.ToUpperInvariant()), arch);
        UpdateDownloader.SelectAsset(TypicalAssets, platform)!.Name.Should().Be(expected);
    }

    [Fact]
    public void SelectAsset_FallsBack_AndReturnsNullWhenNothingFits()
    {
        var linuxX64 = new UpdatePlatform(OSPlatform.Linux, Architecture.X64);
        UpdateDownloader.SelectAsset([new("mRemoteNG-1.78.0-linux-x64.tar.gz", "u", 1), new("mRemoteNG-1.78.0-x64.msi", "u", 1)], linuxX64)!
            .Name.Should().EndWith(".tar.gz");
        UpdateDownloader.SelectAsset([new("mRemoteNG-1.78.0-x64.msi", "u", 1), new("SHA256SUMS", "u", 1)], linuxX64).Should().BeNull();
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  app.AppImage\n", false)]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF *app.AppImage", false)]
    [InlineData("SHA256 (app.AppImage) = 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    public void ParseChecksumFile_SupportsCommonFormats(string text, bool singleFile)
    {
        UpdateDownloader.ParseChecksumFile(text, "app.AppImage", singleFile)
            .Should().Be("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
    }

    [Fact]
    public async Task CheckThenDownload_FromAFakeGitHub_VerifiesTheDigest()
    {
        var payload = RandomNumberGenerator.GetBytes(300_000);
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        _server.Map("/download/app-x86_64.AppImage", payload);
        _server.Map("/api/releases", $$"""
            [ { "tag_name": "v9.9.0", "prerelease": false, "draft": false, "html_url": "https://example/rel",
                "assets": [ { "name": "app-x86_64.AppImage", "size": {{payload.Length}}, "digest": "sha256:{{hash}}",
                              "browser_download_url": "{{_server.BaseUrl}}/download/app-x86_64.AppImage" } ] } ]
            """);

        using var http = DirectClient();
        var check = await new UpdateChecker(http, _server.BaseUrl + "/api/releases").CheckAsync(new Version(1, 0), UpdateChannel.Stable);
        check.IsUpdateAvailable.Should().BeTrue();
        var asset = UpdateDownloader.SelectAsset(check.Assets, new UpdatePlatform(OSPlatform.Linux, Architecture.X64))!;
        asset.Sha256.Should().Be(hash);

        var progress = new List<double>();
        var result = await new UpdateDownloader(http).DownloadAsync(asset, check.Assets, _dir.Path, new SyncProgress(progress.Add));

        result.Succeeded.Should().BeTrue(result.Message);
        result.Verified.Should().BeTrue();
        File.ReadAllBytes(result.FilePath!).Should().Equal(payload);
        progress.Should().NotBeEmpty().And.EndWith(1.0);
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(result.FilePath!).Should().HaveFlag(UnixFileMode.UserExecute, "AppImages are made executable");
    }

    [Fact]
    public async Task Download_UsesAPublishedChecksumFile_AndDeletesAMismatch()
    {
        var payload = Encoding.UTF8.GetBytes("package contents");
        _server.Map("/pkg.deb", payload);
        _server.Map("/SHA256SUMS", $"{new string('0', 64)}  pkg.deb\n");
        var assets = new List<ReleaseAsset>
        {
            new("pkg.deb", _server.BaseUrl + "/pkg.deb", payload.Length),
            new("SHA256SUMS", _server.BaseUrl + "/SHA256SUMS", 80),
        };

        using var http = DirectClient();
        var result = await new UpdateDownloader(http).DownloadAsync(assets[0], assets, _dir.Path);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("SHA-256 mismatch");
        Directory.GetFiles(_dir.Path).Should().BeEmpty("a file that fails verification is removed");

        _server.Map("/SHA256SUMS", $"{Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant()}  pkg.deb\n");
        var ok = await new UpdateDownloader(http).DownloadAsync(assets[0], assets, _dir.Path);
        ok.Succeeded.Should().BeTrue();
        ok.Verified.Should().BeTrue();
    }

    [Fact]
    public async Task Download_RejectsASizeMismatch_AndReportsUnverifiedDownloads()
    {
        _server.Map("/a.zip", "12345");
        using var http = DirectClient();
        var downloader = new UpdateDownloader(http);

        var wrongSize = await downloader.DownloadAsync(new ReleaseAsset("a.zip", _server.BaseUrl + "/a.zip", 99), [], _dir.Path);
        wrongSize.Succeeded.Should().BeFalse();
        wrongSize.Message.Should().Contain("99");

        var unverified = await downloader.DownloadAsync(new ReleaseAsset("a.zip", _server.BaseUrl + "/a.zip", 5), [], _dir.Path);
        unverified.Succeeded.Should().BeTrue();
        unverified.Verified.Should().BeFalse();
        unverified.Message.Should().Contain("no checksum");
    }

    [Fact]
    public async Task Download_ReportsHttpErrors()
    {
        using var http = DirectClient();
        var result = await new UpdateDownloader(http).DownloadAsync(new ReleaseAsset("x.msi", _server.BaseUrl + "/missing", 0), [], _dir.Path);
        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("404");
    }

    [Fact]
    public async Task ProxySettings_RouteRequestsThroughTheProxy_WithCredentials()
    {
        _server.Map("/api/releases", """[ { "tag_name": "v2.0.0", "prerelease": false, "draft": false, "assets": [] } ]""");
        _server.RequiredProxyAuthorization = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("proxyuser:proxypass"));
        var proxy = new UpdateProxySettings(true, "127.0.0.1", _server.Port, UseAuthentication: true, "proxyuser", "proxypass");
        proxy.ProxyUri.Should().Be(new Uri($"http://127.0.0.1:{_server.Port}/"));

        using var http = new HttpClient(proxy.CreateHandler());
        var result = await new UpdateChecker(http, "http://releases.invalid/api/releases").CheckAsync(new Version(1, 0), UpdateChannel.Stable);

        result.Succeeded.Should().BeTrue(result.Message);
        _server.Requests.Select(r => r.RequestLine).Should().Contain(l => l.StartsWith("GET http://releases.invalid/api/releases", StringComparison.Ordinal),
            "the request went to the proxy with an absolute URI");
    }

    [Fact]
    public void ProxySettings_WithoutProxy_HasNoUri()
    {
        UpdateProxySettings.None.ProxyUri.Should().BeNull();
        new UpdateProxySettings(true, "", 8080).ProxyUri.Should().BeNull();
        new UpdateProxySettings(true, "http://proxy.corp", 3128).ProxyUri.Should().Be(new Uri("http://proxy.corp:3128/"));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
