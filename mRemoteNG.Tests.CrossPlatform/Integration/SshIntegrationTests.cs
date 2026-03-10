using System;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Integration;

/// <summary>
/// SSH integration tests. These require a live SSH server.
/// Set env vars: SSH_TEST_HOST, SSH_TEST_PORT, SSH_TEST_USER, SSH_TEST_PASS
/// Run with: dotnet test --filter Category=Integration
/// </summary>
[Trait("Category", "Integration")]
public class SshIntegrationTests
{
    private readonly string? _host = Environment.GetEnvironmentVariable("SSH_TEST_HOST");
    private readonly string? _user = Environment.GetEnvironmentVariable("SSH_TEST_USER");
    private readonly string? _pass = Environment.GetEnvironmentVariable("SSH_TEST_PASS");
    private readonly int _port = int.TryParse(Environment.GetEnvironmentVariable("SSH_TEST_PORT"), out var p) ? p : 22;

    private bool CanRun => !string.IsNullOrEmpty(_host) && !string.IsNullOrEmpty(_user);

    [SkippableFact]
    public async Task SshProtocol_Connect_Succeeds()
    {
        Skip.IfNot(CanRun, "SSH_TEST_HOST / SSH_TEST_USER not set");
        // TODO: instantiate SshNetProtocol, connect, verify state == Connected
        await Task.CompletedTask;
    }

    [SkippableFact]
    public async Task SftpBrowser_ListDirectory_ReturnsEntries()
    {
        Skip.IfNot(CanRun, "SSH_TEST_HOST / SSH_TEST_USER not set");
        // TODO: connect SFTP, list /, verify at least one entry
        await Task.CompletedTask;
    }
}
