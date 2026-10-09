using System.Net;
using FluentAssertions;
using mRemoteNG.Core.Settings;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Settings;

public sealed class UpdateCheckerTests
{
    private const string Releases = """
        [
          { "tag_name": "v1.78.0-beta1", "name": "1.78.0 beta 1", "prerelease": true, "draft": false, "html_url": "https://example/beta" },
          { "tag_name": "v1.79.0", "name": "draft", "prerelease": false, "draft": true, "html_url": "https://example/draft" },
          { "tag_name": "v1.77.3", "name": "mRemoteNG 1.77.3", "prerelease": false, "draft": false, "html_url": "https://example/stable" },
          { "tag_name": "nightly", "name": "Nightly build", "prerelease": true, "draft": false, "html_url": "https://example/none" },
          { "tag_name": "v1.76.20", "name": "old", "prerelease": false, "draft": false, "html_url": "https://example/old" }
        ]
        """;

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network");
    }

    [Theory]
    [InlineData("v1.77.3", "1.77.3")]
    [InlineData("1.76.20", "1.76.20")]
    [InlineData("20240627-v1.77.0-NB-(2948)", "1.77.0")]
    [InlineData("v1.78.2-dev", "1.78.2")]
    [InlineData("nightly", null)]
    public void ParseVersion_ExtractsDottedVersion(string text, string? expected)
    {
        UpdateChecker.ParseVersion(text)?.ToString().Should().Be(expected);
    }

    [Fact]
    public async Task Stable_IgnoresPreReleasesAndDrafts()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Releases);
        var checker = new UpdateChecker(new HttpClient(handler));

        var result = await checker.CheckAsync(new Version(1, 76, 0), UpdateChannel.Stable);

        result.Succeeded.Should().BeTrue();
        result.IsUpdateAvailable.Should().BeTrue();
        result.LatestTag.Should().Be("v1.77.3");
        result.ReleaseUrl.Should().Be("https://example/stable");
        handler.Request!.Headers.UserAgent.Should().NotBeEmpty("GitHub rejects requests without a User-Agent");
    }

    [Fact]
    public async Task PreRelease_IncludesPreReleases()
    {
        var checker = new UpdateChecker(new HttpClient(new FakeHandler(HttpStatusCode.OK, Releases)));

        var result = await checker.CheckAsync(new Version(1, 77, 3), UpdateChannel.PreRelease);

        result.IsUpdateAvailable.Should().BeTrue();
        result.LatestTag.Should().Be("v1.78.0-beta1");
    }

    [Fact]
    public async Task CurrentVersionIsNewest_ReportsUpToDate()
    {
        var checker = new UpdateChecker(new HttpClient(new FakeHandler(HttpStatusCode.OK, Releases)));

        var result = await checker.CheckAsync(new Version(1, 78, 2), UpdateChannel.Stable);

        result.Succeeded.Should().BeTrue();
        result.IsUpdateAvailable.Should().BeFalse();
        result.Message.Should().Contain("latest version");
    }

    [Fact]
    public async Task HttpError_IsReportedNotThrown()
    {
        var checker = new UpdateChecker(new HttpClient(new FakeHandler(HttpStatusCode.Forbidden, "{}")));

        var result = await checker.CheckAsync(new Version(1, 0), UpdateChannel.Stable);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("403");
    }

    [Fact]
    public async Task NetworkFailure_IsReportedNotThrown()
    {
        var checker = new UpdateChecker(new HttpClient(new ThrowingHandler()));

        var result = await checker.CheckAsync(new Version(1, 0), UpdateChannel.Stable);

        result.Succeeded.Should().BeFalse();
        result.Message.Should().Contain("no network");
    }
}
