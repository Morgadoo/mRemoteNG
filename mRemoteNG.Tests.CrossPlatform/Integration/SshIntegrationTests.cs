using System.Diagnostics;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Input;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;
using NSubstitute;
using Xunit;
using SocketException = System.Net.Sockets.SocketException;
using TcpClient = System.Net.Sockets.TcpClient;

namespace mRemoteNG.Tests.CrossPlatform.Integration;

/// <summary>
/// Starts a throw-away OpenSSH server on 127.0.0.1:2222 (root only, key authentication for root
/// via a temporary authorized_keys file). Tests are skipped when sshd/ssh-keygen are missing, the
/// process is not root, or the port is taken.
/// </summary>
public sealed class SshdFixture : IDisposable
{
    public const int Port = 2222;
    private const string Sshd = "/usr/sbin/sshd";

    private readonly Process? _sshd;

    public SshdFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "mremoteng-sshd-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);

        SkipReason = FindSkipReason();
        if (SkipReason is not null)
            return;

        try
        {
            HostKeyPath = Path.Combine(Directory, "host_ed25519");
            ClientKeyPath = Path.Combine(Directory, "client_ed25519");
            RunKeygen(HostKeyPath);
            RunKeygen(ClientKeyPath);
            File.Copy(ClientKeyPath + ".pub", Path.Combine(Directory, "authorized_keys"));
            HostPublicKey = File.ReadAllText(HostKeyPath + ".pub").Split(' ')[1];

            var config = Path.Combine(Directory, "sshd_config");
            File.WriteAllText(config, $"""
                ListenAddress 127.0.0.1
                PidFile {Path.Combine(Directory, "sshd.pid")}
                AuthorizedKeysFile {Path.Combine(Directory, "authorized_keys")}
                PermitRootLogin prohibit-password
                PasswordAuthentication no
                KbdInteractiveAuthentication no
                UsePAM no
                StrictModes no
                Subsystem sftp internal-sftp
                """);
            System.IO.Directory.CreateDirectory("/run/sshd"); // privilege separation directory

            _sshd = Process.Start(new ProcessStartInfo(Sshd)
            {
                ArgumentList = { "-D", "-e", "-p", Port.ToString(), "-f", config, "-h", HostKeyPath },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            _sshd!.BeginErrorReadLine();
            _sshd.BeginOutputReadLine();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!PortOpen())
            {
                if (_sshd.HasExited || DateTime.UtcNow > deadline)
                {
                    SkipReason = "sshd did not start";
                    return;
                }
                Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            SkipReason = "Could not start sshd: " + ex.Message;
        }
    }

    public string Directory { get; }
    public string? SkipReason { get; private set; }
    public string HostKeyPath { get; private set; } = string.Empty;
    public string ClientKeyPath { get; private set; } = string.Empty;
    public string HostPublicKey { get; private set; } = string.Empty;

    public ConnectionParameters Parameters(string? username = "root", Dictionary<string, string>? extras = null) => new()
    {
        Hostname = "127.0.0.1",
        Port = Port,
        Protocol = ProtocolType.Ssh,
        Username = username,
        PrivateKeyPath = ClientKeyPath,
        Extras = extras ?? [],
    };

    private static string? FindSkipReason()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return "sshd integration tests run on Linux/macOS only";
        if (!File.Exists(Sshd) || FindOnPath("ssh-keygen") is null)
            return "OpenSSH server (sshd) or ssh-keygen is not installed";
        if (Environment.UserName != "root")
            return "sshd integration tests must run as root";
        if (PortOpen())
            return $"port {Port} is already in use";
        return null;
    }

    private static string? FindOnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, tool))
            .FirstOrDefault(File.Exists);

    private static void RunKeygen(string path)
    {
        using var keygen = Process.Start(new ProcessStartInfo("ssh-keygen")
        {
            ArgumentList = { "-q", "-t", "ed25519", "-N", "", "-C", "", "-f", path },
            RedirectStandardOutput = true,
        })!;
        keygen.WaitForExit();
        if (keygen.ExitCode != 0)
            throw new InvalidOperationException("ssh-keygen failed");
    }

    private static bool PortOpen()
    {
        try
        {
            using var tcp = new TcpClient();
            tcp.Connect("127.0.0.1", Port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_sshd is { HasExited: false })
        {
            _sshd.Kill(entireProcessTree: true);
            _sshd.WaitForExit(5000);
        }
        _sshd?.Dispose();
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { }
    }
}

/// <summary>
/// End-to-end SSH tests against a real OpenSSH server: host key verification, shell I/O, window
/// size changes, the opening command, username prompting and SFTP transfers.
/// Run only these with: dotnet test --filter Category=Integration
/// </summary>
[Trait("Category", "Integration")]
public sealed class SshIntegrationTests : IClassFixture<SshdFixture>, IDisposable
{
    private readonly SshdFixture _sshd;
    private readonly string _knownHosts;
    private readonly KnownHostsStore _store;
    private readonly ISshUserPrompt _prompt = Substitute.For<ISshUserPrompt>();

    public SshIntegrationTests(SshdFixture sshd)
    {
        _sshd = sshd;
        _knownHosts = Path.Combine(sshd.Directory, "known_hosts-" + Guid.NewGuid().ToString("N"));
        _store = new KnownHostsStore(_knownHosts);
    }

    public void Dispose() => File.Delete(_knownHosts);

    private KnownHostsHostKeyVerifier NewVerifier() => new(_store, _prompt);

    private SshNetProtocol NewProtocol(out TerminalView view)
    {
        var protocol = new SshNetProtocol(NullLogger<SshNetProtocol>.Instance, NewVerifier(), _prompt);
        view = (TerminalView)protocol.CreateView();
        return protocol;
    }

    private static async Task WaitForScreenAsync(TerminalView view, string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!view.GetScreenText().Contains(text, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"'{text}' did not appear. Screen:\n{view.GetScreenText()}");
            await Task.Delay(50);
        }
    }

    [SkippableFact]
    public async Task HostKey_UnknownIsStored_ThenTrustedSilently_ThenTamperedKeyIsRefused()
    {
        Skip.If(_sshd.SkipReason is not null, _sshd.SkipReason);
        HostKeyPromptRequest? asked = null;
        _prompt.ConfirmHostKey(Arg.Do<HostKeyPromptRequest>(r => asked = r)).Returns(HostKeyDecision.AcceptAndSave);

        // 1. Unknown key: the user is asked, accepts, and the key is written to known_hosts.
        using (var protocol = NewProtocol(out _))
        {
            await protocol.ConnectAsync(_sshd.Parameters());
            protocol.State.Should().Be(ConnectionState.Connected);
            await protocol.DisconnectAsync();
        }
        asked!.Status.Should().Be(HostKeyStatus.Unknown);
        asked.HostKey.KeyType.Should().Be("ssh-ed25519");
        asked.HostKey.KeyBase64.Should().Be(_sshd.HostPublicKey);
        File.ReadAllText(_knownHosts).Should().Be($"[127.0.0.1]:{SshdFixture.Port} ssh-ed25519 {_sshd.HostPublicKey}\n");

        // 2. Known key, new verifier (as after an app restart): no prompt.
        _prompt.ClearReceivedCalls();
        using (var protocol = NewProtocol(out _))
        {
            await protocol.ConnectAsync(_sshd.Parameters());
            protocol.State.Should().Be(ConnectionState.Connected);
            await protocol.DisconnectAsync();
        }
        _prompt.DidNotReceiveWithAnyArgs().ConfirmHostKey(default!);

        // 3. Stored key tampered with: refused as a mismatch, nothing replaced.
        var tampered = Convert.FromBase64String(_sshd.HostPublicKey);
        tampered[^1] ^= 0xFF;
        var tamperedLine = $"[127.0.0.1]:{SshdFixture.Port} ssh-ed25519 {Convert.ToBase64String(tampered)}\n";
        File.WriteAllText(_knownHosts, tamperedLine);
        _prompt.ConfirmHostKey(Arg.Do<HostKeyPromptRequest>(r => asked = r)).Returns(HostKeyDecision.Reject);

        using (var protocol = NewProtocol(out var view))
        {
            var connect = () => protocol.ConnectAsync(_sshd.Parameters());
            (await connect.Should().ThrowAsync<HostKeyVerificationException>())
                .WithMessage("*has changed*");
            protocol.State.Should().Be(ConnectionState.Error);
            view.GetScreenText().Should().Contain("Host key verification failed");
        }
        asked.Status.Should().Be(HostKeyStatus.Mismatch);
        File.ReadAllText(_knownHosts).Should().Be(tamperedLine);
    }

    [SkippableFact]
    public async Task Shell_RunsOpeningCommand_ForwardsInput_AndSendsWindowSize()
    {
        Skip.If(_sshd.SkipReason is not null, _sshd.SkipReason);
        _store.Add(new HostKeyInfo("127.0.0.1", SshdFixture.Port, Convert.FromBase64String(_sshd.HostPublicKey)));

        using var protocol = NewProtocol(out var view);
        var extras = new Dictionary<string, string>
        {
            [ConnectionParametersFactory.Keys.OpeningCommand] = "echo opening-$((6*7))",
        };
        await protocol.ConnectAsync(_sshd.Parameters(extras: extras));

        // Output of the opening command (computed remotely, so it is not just the echoed input)
        await WaitForScreenAsync(view, "opening-42");

        // Keyboard input travels through the view to the shell
        view.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "echo typed-$((40+2))\r" });
        await WaitForScreenAsync(view, "typed-42");

        // Laying the view out at a new size sends window-change to the server
        view.Measure(new Size(840, 480));
        view.Arrange(new Rect(0, 0, 840, 480));
        (view.TerminalCols, view.TerminalRows).Should().Be((100, 30));
        view.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "stty size\r" });
        await WaitForScreenAsync(view, "30 100");

        // Escape sequences in remote output are interpreted, not printed: the output line is just the text
        view.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "printf '\\033[31mred\\033[0m-done\\n'\r" });
        await WaitForScreenAsync(view, "\nred-done\n");

        await protocol.DisconnectAsync();
        protocol.State.Should().Be(ConnectionState.Disconnected);
    }

    [SkippableFact]
    public async Task MissingUsername_IsAskedFor_OrFailsClearly()
    {
        Skip.If(_sshd.SkipReason is not null, _sshd.SkipReason);
        _store.Add(new HostKeyInfo("127.0.0.1", SshdFixture.Port, Convert.FromBase64String(_sshd.HostPublicKey)));

        _prompt.PromptTextAsync(Arg.Is<SshTextPrompt>(p => !p.IsSecret), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("root"));
        using (var protocol = NewProtocol(out _))
        {
            await protocol.ConnectAsync(_sshd.Parameters(username: null));
            protocol.State.Should().Be(ConnectionState.Connected);
            await protocol.DisconnectAsync();
        }

        _prompt.PromptTextAsync(Arg.Any<SshTextPrompt>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        using (var protocol = NewProtocol(out _))
        {
            var connect = () => protocol.ConnectAsync(_sshd.Parameters(username: null));
            await connect.Should().ThrowAsync<InvalidOperationException>().WithMessage("No username is configured*");
        }
    }

    [SkippableFact]
    public async Task SlowHostKeyDecision_ReconnectsAfterHandshakeTimeout()
    {
        Skip.If(_sshd.SkipReason is not null, _sshd.SkipReason);
        _prompt.ConfirmHostKey(Arg.Any<HostKeyPromptRequest>()).Returns(_ =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(6)); // longer than the handshake timeout below
            return HostKeyDecision.AcceptOnce;
        });

        var original = SshConnector.OperationTimeout;
        SshConnector.OperationTimeout = TimeSpan.FromSeconds(3);
        try
        {
            using var protocol = NewProtocol(out _);
            await protocol.ConnectAsync(_sshd.Parameters());
            protocol.State.Should().Be(ConnectionState.Connected);
            await protocol.DisconnectAsync();
        }
        finally
        {
            SshConnector.OperationTimeout = original;
        }

        _prompt.ReceivedWithAnyArgs(1).ConfirmHostKey(default!);
        File.Exists(_knownHosts).Should().BeFalse("'connect once' does not store the key");
    }

    [SkippableFact]
    public async Task Sftp_UploadListDownloadMkdirDelete_RoundTrip()
    {
        Skip.If(_sshd.SkipReason is not null, _sshd.SkipReason);
        _prompt.ConfirmHostKey(Arg.Any<HostKeyPromptRequest>()).Returns(HostKeyDecision.AcceptAndSave);

        var payload = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 123);
        var localUpload = Path.Combine(_sshd.Directory, "upload.bin");
        var localDownload = Path.Combine(_sshd.Directory, "download.bin");
        await File.WriteAllBytesAsync(localUpload, payload);

        using var sftp = new SftpSession(NewVerifier(), _prompt);
        await sftp.ConnectAsync(_sshd.Parameters());
        sftp.IsConnected.Should().BeTrue();
        sftp.HomeDirectory.Should().Be("/root");

        var remoteDir = SftpSession.CombinePath(_sshd.Directory, "remote");
        await sftp.CreateDirectoryAsync(remoteDir);
        await sftp.CreateDirectoryAsync(SftpSession.CombinePath(remoteDir, "sub"));
        var remoteFile = SftpSession.CombinePath(remoteDir, "file.bin");

        var uploadProgress = new List<TransferProgress>();
        await sftp.UploadFileAsync(localUpload, remoteFile, new SyncProgress(uploadProgress));
        uploadProgress.Should().HaveCountGreaterThan(2);
        uploadProgress[^1].Should().Be(new TransferProgress(payload.Length, payload.Length));

        var listing = await sftp.ListDirectoryAsync(remoteDir);
        listing.Select(e => (e.Name, e.IsDirectory)).Should().Equal(("sub", true), ("file.bin", false));
        listing[1].Length.Should().Be(payload.Length);

        var downloadProgress = new List<TransferProgress>();
        await sftp.DownloadFileAsync(remoteFile, localDownload, new SyncProgress(downloadProgress));
        (await File.ReadAllBytesAsync(localDownload)).Should().Equal(payload);
        downloadProgress[^1].Percent.Should().Be(100);

        var missing = () => sftp.DownloadFileAsync(remoteFile + ".missing", localDownload + ".2");
        await missing.Should().ThrowAsync<Exception>();
        File.Exists(localDownload + ".2").Should().BeFalse();

        await sftp.DeleteAsync(new RemoteFileEntry("remote", remoteDir, true, false, 0, DateTime.MinValue));
        (await sftp.ListDirectoryAsync(_sshd.Directory)).Should().NotContain(e => e.Name == "remote");

        await sftp.DisconnectAsync();
        sftp.IsConnected.Should().BeFalse();
    }

    private sealed class SyncProgress(List<TransferProgress> target) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => target.Add(value);
    }
}
