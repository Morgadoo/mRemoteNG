using FluentAssertions;
using mRemoteNG.Protocols.Ssh;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols;

/// <summary>
/// Unit tests for <see cref="SshConfigImporter"/>.
/// Uses in-memory temp files — no network or SSH daemon needed.
/// </summary>
public sealed class SshConfigImporterTests : IDisposable
{
    private readonly string _configFile;

    public SshConfigImporterTests()
    {
        _configFile = Path.GetTempFileName();
    }

    [Fact]
    public async Task Import_ShouldParseHostname()
    {
        await WriteConfig("""
            Host webserver
              HostName 192.168.1.10
              User admin
              Port 2222
            """);

        var entries = await SshConfigImporter.ImportAsync(_configFile);

        entries.Should().ContainSingle();
        var entry = entries[0];
        entry.Alias.Should().Be("webserver");
        entry.Hostname.Should().Be("192.168.1.10");
        entry.Username.Should().Be("admin");
        entry.Port.Should().Be(2222);
    }

    [Fact]
    public async Task Import_ShouldDefaultPort22_WhenNotSpecified()
    {
        await WriteConfig("""
            Host myserver
              HostName 10.0.0.1
            """);

        var entries = await SshConfigImporter.ImportAsync(_configFile);
        entries[0].Port.Should().Be(22);
    }

    [Fact]
    public async Task Import_ShouldSetHostname_ToAlias_WhenHostNameAbsent()
    {
        await WriteConfig("""
            Host 10.0.0.5
              User root
            """);

        var entries = await SshConfigImporter.ImportAsync(_configFile);
        entries[0].Hostname.Should().Be("10.0.0.5");
    }

    [Fact]
    public async Task Import_ShouldParseIdentityFile()
    {
        await WriteConfig("""
            Host bastion
              HostName bastion.example.com
              IdentityFile ~/.ssh/id_rsa
            """);

        var entries = await SshConfigImporter.ImportAsync(_configFile);
        entries[0].IdentityFile.Should()
            .NotBeNullOrEmpty()
            .And.NotContain("~", "tilde should be expanded");
    }

    [Fact]
    public async Task Import_ShouldSkipWildcardHosts()
    {
        await WriteConfig("""
            Host *
              ServerAliveInterval 60

            Host myhost
              HostName 192.168.1.1
            """);

        var entries = await SshConfigImporter.ImportAsync(_configFile);
        entries.Should().ContainSingle("wildcard Host * must be skipped");
        entries[0].Alias.Should().Be("myhost");
    }

    [Fact]
    public async Task Import_ShouldParseMultipleHosts()
    {
        await WriteConfig("""
            Host alpha
              HostName 10.0.0.1

            Host beta
              HostName 10.0.0.2
              Port 2200

            Host gamma
              HostName 10.0.0.3
              User ubuntu
            """);

        var entries = await SshConfigImporter.ImportAsync(_configFile);
        entries.Should().HaveCount(3);
        entries.Select(e => e.Alias).Should().Equal("alpha", "beta", "gamma");
        entries[1].Port.Should().Be(2200);
        entries[2].Username.Should().Be("ubuntu");
    }

    [Fact]
    public async Task Import_ShouldReturnEmpty_WhenFileDoesNotExist()
    {
        var entries = await SshConfigImporter.ImportAsync("/nonexistent/path/.ssh/config");
        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_ShouldIgnoreCommentLines()
    {
        await WriteConfig("""
            # This is a comment
            Host real-host
              # Another comment
              HostName 192.168.1.50
            """);

        var entries = await SshConfigImporter.ImportAsync(_configFile);
        entries.Should().ContainSingle();
        entries[0].Hostname.Should().Be("192.168.1.50");
    }

    private Task WriteConfig(string content) =>
        File.WriteAllTextAsync(_configFile, content.Trim());

    public void Dispose()
    {
        if (File.Exists(_configFile))
            File.Delete(_configFile);
    }
}
