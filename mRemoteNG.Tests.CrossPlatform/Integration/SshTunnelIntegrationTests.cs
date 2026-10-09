using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Config.Putty;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree.Root;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;
using mRemoteNG.Protocols.Telnet;
using NSubstitute;
using Renci.SshNet;
using Xunit;
using ConnectionInfo = mRemoteNG.Core.Connection.ConnectionInfo;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Integration;

/// <summary>
/// A throw-away OpenSSH server on 127.0.0.1:2230 with TCP forwarding enabled (root only, key
/// authentication via a temporary authorized_keys file). Skipped when sshd/ssh-keygen are missing,
/// the process is not root, or the port is taken.
/// </summary>
public sealed class TunnelSshdFixture : IDisposable
{
    public const int Port = 2230;
    private const string Sshd = "/usr/sbin/sshd";

    private readonly Process? _sshd;

    public TunnelSshdFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "mremoteng-tunnel-sshd-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);

        SkipReason = FindSkipReason();
        if (SkipReason is not null)
            return;

        try
        {
            var hostKey = Path.Combine(Directory, "host_ed25519");
            ClientKeyPath = Path.Combine(Directory, "client_ed25519");
            RunKeygen(hostKey);
            RunKeygen(ClientKeyPath);
            File.Copy(ClientKeyPath + ".pub", Path.Combine(Directory, "authorized_keys"));
            HostPublicKey = File.ReadAllText(hostKey + ".pub").Split(' ')[1];

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
                AllowTcpForwarding yes
                Compression yes
                """);
            System.IO.Directory.CreateDirectory("/run/sshd");

            _sshd = Process.Start(new ProcessStartInfo(Sshd)
            {
                ArgumentList = { "-D", "-e", "-p", Port.ToString(), "-f", config, "-h", hostKey },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            _sshd!.BeginErrorReadLine();
            _sshd.BeginOutputReadLine();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!TcpTestServers.PortOpen(Port))
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
    public string ClientKeyPath { get; private set; } = string.Empty;
    public string HostPublicKey { get; private set; } = string.Empty;

    /// <summary>A tree node for this server, authenticating with the client key through the SSH options.</summary>
    public ConnectionInfo SshConnection(string name, string sshOptions = "") => new()
    {
        Name = name,
        Protocol = CoreProtocol.SSH2,
        Hostname = "127.0.0.1",
        Port = Port,
        Username = "root",
        SSHOptions = $"-i \"{ClientKeyPath}\" {sshOptions}",
    };

    private static string? FindSkipReason()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return "sshd integration tests run on Linux/macOS only";
        if (!File.Exists(Sshd) || !File.Exists("/usr/bin/ssh-keygen"))
            return "OpenSSH server (sshd) or ssh-keygen is not installed";
        if (Environment.UserName != "root")
            return "sshd integration tests must run as root";
        if (TcpTestServers.PortOpen(Port))
            return $"port {Port} is already in use";
        return null;
    }

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

/// <summary>In-process TCP servers used as tunnel targets and proxies.</summary>
internal sealed class TcpTestServers : IDisposable
{
    private readonly List<TcpListener> _listeners = [];
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Requests seen by <see cref="StartHttpConnectProxy"/>.</summary>
    public List<string> ProxyRequests { get; } = [];

    public static bool PortOpen(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            tcp.Connect("127.0.0.1", port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Echoes every line it receives in upper case (so the reply differs from a local echo).</summary>
    public int StartUppercaseEcho() => Start(async client =>
    {
        var stream = client.GetStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, _cts.Token)) > 0)
        {
            var reply = Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(buffer, 0, read).ToUpperInvariant());
            await stream.WriteAsync(reply, _cts.Token);
        }
    });

    /// <summary>A minimal HTTP CONNECT proxy.</summary>
    public int StartHttpConnectProxy() => Start(async client =>
    {
        var stream = client.GetStream();
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one, _cts.Token) == 1)
            header.Append((char)one[0]);
        var requestLine = header.ToString().Split("\r\n")[0];
        lock (ProxyRequests)
            ProxyRequests.Add(requestLine);

        var target = requestLine.Split(' ')[1].Split(':');
        using var upstream = new TcpClient();
        await upstream.ConnectAsync(target[0], int.Parse(target[1]), _cts.Token);
        await stream.WriteAsync("HTTP/1.1 200 Connection established\r\n\r\n"u8.ToArray(), _cts.Token);
        var upstreamStream = upstream.GetStream();
        await Task.WhenAny(stream.CopyToAsync(upstreamStream, _cts.Token), upstreamStream.CopyToAsync(stream, _cts.Token));
    });

    private int Start(Func<TcpClient, Task> handle)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (Exception)
                {
                    return;
                }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try { await handle(client); }
                        catch (Exception) { /* client went away */ }
                    }
                });
            }
        });
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var listener in _listeners)
            listener.Stop();
        _cts.Dispose();
    }
}

/// <summary>
/// SSH tunnels (SSHTunnelConnectionName), SSH options (forwardings, compression, -N), proxies and
/// programmatic terminal input against a real OpenSSH server.
/// Run only these with: dotnet test --filter Category=Integration
/// </summary>
[Trait("Category", "Integration")]
public sealed class SshTunnelIntegrationTests : IClassFixture<TunnelSshdFixture>, IDisposable
{
    private readonly TunnelSshdFixture _sshd;
    private readonly KnownHostsStore _store;
    private readonly ISshUserPrompt _prompt = Substitute.For<ISshUserPrompt>();
    private readonly TcpTestServers _servers = new();
    private readonly ConnectionPreparer _preparer;
    private readonly RootNodeInfo _root = new(RootNodeType.Connection);

    public SshTunnelIntegrationTests(TunnelSshdFixture sshd)
    {
        _sshd = sshd;
        _store = new KnownHostsStore(Path.Combine(sshd.Directory, "known_hosts-" + Guid.NewGuid().ToString("N")));
        if (sshd.SkipReason is null)
            _store.Add(new HostKeyInfo("127.0.0.1", TunnelSshdFixture.Port, Convert.FromBase64String(sshd.HostPublicKey)));
        _prompt.ConfirmHostKey(Arg.Any<HostKeyPromptRequest>()).Returns(HostKeyDecision.Reject);

        ConnectionPreparer? preparer = null;
        preparer = new ConnectionPreparer(
        [
            new SshSettingsPreparationStep(new PuttySessionCatalog(new PuttySessionFilesProvider(Path.Combine(sshd.Directory, "no-putty")))),
            new SshTunnelPreparationStep(NewVerifier(), _prompt, () => preparer),
        ]);
        _preparer = preparer;
    }

    public void Dispose() => _servers.Dispose();

    private KnownHostsHostKeyVerifier NewVerifier() => new(_store, _prompt);

    private T Add<T>(T node, ContainerInfo? parent = null) where T : ConnectionInfo
    {
        (parent ?? _root).AddChild(node);
        return node;
    }

    private ConnectionInfo RawTarget(int port, string tunnel) => new()
    {
        Name = "echo",
        Protocol = CoreProtocol.RAW,
        Hostname = "127.0.0.1",
        Port = port,
        SSHTunnelConnectionName = tunnel,
    };

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

    private static async Task<string> ExchangeAsync(int port, string text)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text));
        return await ReadAsync(stream, text.Length);
    }

    private static async Task<string> ReadAsync(NetworkStream stream, int length)
    {
        var buffer = new byte[length];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.ReadExactlyAsync(buffer, timeout.Token);
        return Encoding.ASCII.GetString(buffer);
    }

    [SkippableFact]
    public async Task RawTarget_ThroughTunnel_DataFlows_AndClosingReleasesTheForward()
    {
        Skip.If(_sshd.SkipReason is not null, _sshd.SkipReason);
        var echoPort = _servers.StartUppercaseEcho();
        var folder = Add(new ContainerInfo { Name = "servers" });
        Add(_sshd.SshConnection("jump"), folder);
        var target = Add(RawTarget(echoPort, "jump"));

        var prepared = await _preparer.PrepareAsync(target);

        prepared.Parameters.Hostname.Should().Be("127.0.0.1");
        prepared.Parameters.Port.Should().NotBe(echoPort);
        prepared.Parameters.Extras[SshExtras.TunnelVia].Should().Be("jump");
        var tunnel = prepared.Resources.OfType<SshTunnel>().Should().ContainSingle().Subject;
        tunnel.IsOpen.Should().BeTrue();
        (tunnel.LocalPort, tunnel.TargetHost, tunnel.TargetPort).Should().Be((prepared.Parameters.Port, "127.0.0.1", echoPort));

        using (var raw = new RawSocketProtocol(NullLogger<RawSocketProtocol>.Instance))
        {
            var view = (TerminalView)raw.CreateView();
            await raw.ConnectAsync(prepared.Parameters);
            await ((ITerminalProtocol)raw).SendInputAsync("through the tunnel\r"u8.ToArray());
            await WaitForScreenAsync(view, "THROUGH THE TUNNEL");
            await raw.DisconnectAsync();
        }

        await prepared.DisposeAsync();
        tunnel.IsOpen.Should().BeFalse();
        TcpTestServers.PortOpen(tunnel.LocalPort).Should().BeFalse("closing the session releases the forwarded port");
    }

    [SkippableFact]
    public async Task SshTarget_ThroughTunnel_ChecksTheRealHostKey_AndAcceptsProgrammaticInput()
    {
        Skip.If(_sshd.SkipReason is not null, _sshd.SkipReason);
        Add(_sshd.SshConnection("jump"));
        var target = Add(_sshd.SshConnection("shell"));
        target.SSHTunnelConnectionName = "jump";

        await using var prepared = await _preparer.PrepareAsync(target);
        using var ssh = new SshNetProtocol(NullLogger<SshNetProtocol>.Instance, NewVerifier(), _prompt);
        var view = (TerminalView)ssh.CreateView();
        await ssh.ConnectAsync(prepared.Parameters);

        // known_hosts only knows [127.0.0.1]:2230, not the random tunnel port: no prompt was needed.
        _prompt.DidNotReceiveWithAnyArgs().ConfirmHostKey(default!);
        view.GetScreenText().Should().Contain($"Connecting to 127.0.0.1:{TunnelSshdFixture.Port} via SSH tunnel \"jump\"");

        await ((ITerminalProtocol)ssh).SendInputAsync("echo via-$((40+2))\r"u8.ToArray());
        await WaitForScreenAsync(view, "via-42");
        await ssh.DisconnectAsync();
    }

    [SkippableFact]
    public async Task ChainedTunnels_Work_AndCyclesAreRefused()
    {
        Skip.If(_sshd.SkipReason is not null, _sshd.SkipReason);
        var echoPort = _servers.StartUppercaseEcho();
        Add(_sshd.SshConnection("outer"));
        var inner = Add(_sshd.SshConnection("inner"));
        inner.SSHTunnelConnectionName = "outer";
        var target = Add(RawTarget(echoPort, "inner"));

        await using (var prepared = await _preparer.PrepareAsync(target))
        {
            prepared.Resources.OfType<PreparedConnection>().Should().ContainSingle()
                .Which.Resources.OfType<SshTunnel>().Should().ContainSingle().Which.Name.Should().Be("outer");
            prepared.Resources.OfType<SshTunnel>().Should().ContainSingle().Which.Name.Should().Be("inner");
            (await ExchangeAsync(prepared.Parameters.Port, "chained\n")).Should().Be("CHAINED\n");
        }

        // outer → inner → outer …
        _root.Children.Single(c => c.Name == "outer").SSHTunnelConnectionName = "inner";
        var act = () => _preparer.PrepareAsync(target);
        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*already part of the tunnel chain*");
    }

    [Fact]
    public async Task MissingOrNonSshTunnel_AndUnsupportedProtocols_FailClearly()
    {
        var rdp = Add(new ConnectionInfo { Name = "desktop", Protocol = CoreProtocol.RDP, Hostname = "h", Port = 3389 });
        var target = Add(RawTarget(1, "nowhere"));

        var missing = () => _preparer.PrepareAsync(target);
        (await missing.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("The SSH tunnel connection \"nowhere\" configured for \"echo\" was not found in the connection tree.");

        target.SSHTunnelConnectionName = "desktop";
        var notSsh = () => _preparer.PrepareAsync(target);
        (await notSsh.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*is a RDP connection; an SSH tunnel must be an SSH connection.");

        var shell = Add(new ConnectionInfo { Name = "local", Protocol = CoreProtocol.Terminal, SSHTunnelConnectionName = rdp.Name });
        var unsupported = () => _preparer.PrepareAsync(shell);
        await unsupported.Should().ThrowAsync<NotSupportedException>().WithMessage("*cannot go through an SSH tunnel*");
    }

    [SkippableFact]
    public async Task SshOptions_StartLocalRemoteAndDynamicForwardings_AndReportUnsupportedOptions()
    {
        Skip.If(_sshd.SkipReason is not null, _sshd.SkipReason);
        var echoPort = _servers.StartUppercaseEcho();
        var connection = Add(_sshd.SshConnection("forwards",
            $"-C -A -L 2241:127.0.0.1:{echoPort} -R 2242:127.0.0.1:{echoPort} -D 2243"));

        await using var prepared = await _preparer.PrepareAsync(connection);
        using var ssh = new SshNetProtocol(NullLogger<SshNetProtocol>.Instance, NewVerifier(), _prompt);
        var view = (TerminalView)ssh.CreateView();
        await ssh.ConnectAsync(prepared.Parameters);

        var screen = view.GetScreenText();
        screen.Should().Contain("Agent forwarding (-A) is not supported");
        screen.Should().Contain($"Port forwarding local 127.0.0.1:2241 → 127.0.0.1:{echoPort}");
        screen.Should().Contain("Port forwarding dynamic (SOCKS) 127.0.0.1:2243");

        (await ExchangeAsync(2241, "local\n")).Should().Be("LOCAL\n");
        (await ExchangeAsync(2242, "remote\n")).Should().Be("REMOTE\n");

        // SOCKS5: no authentication, CONNECT 127.0.0.1:echoPort
        using (var socks = new TcpClient())
        {
            await socks.ConnectAsync("127.0.0.1", 2243);
            var stream = socks.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 });
            (await ReadAsync(stream, 2)).Should().Be("\x05\x00");
            await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(echoPort >> 8), (byte)echoPort });
            var reply = new byte[10];
            await stream.ReadExactlyAsync(reply);
            reply[1].Should().Be(0, "the SOCKS request succeeds");
            await stream.WriteAsync("dynamic\n"u8.ToArray());
            (await ReadAsync(stream, 8)).Should().Be("DYNAMIC\n");
        }

        await ssh.DisconnectAsync();
        TcpTestServers.PortOpen(2241).Should().BeFalse();
        TcpTestServers.PortOpen(2243).Should().BeFalse();
    }

    [SkippableFact]
    public async Task ForwardingThatCannotStart_IsReported_AndTheSessionStillOpens()
    {
        Skip.If(_sshd.SkipReason is not null, _sshd.SkipReason);
        using var occupied = new TcpListener(IPAddress.Loopback, 2244);
        occupied.Start();
        var connection = Add(_sshd.SshConnection("busy", "-L 2244:127.0.0.1:1"));

        await using var prepared = await _preparer.PrepareAsync(connection);
        using var ssh = new SshNetProtocol(NullLogger<SshNetProtocol>.Instance, NewVerifier(), _prompt);
        var view = (TerminalView)ssh.CreateView();
        await ssh.ConnectAsync(prepared.Parameters);

        ssh.State.Should().Be(ConnectionState.Connected);
        view.GetScreenText().Should().Contain("Port forwarding local 127.0.0.1:2244 → 127.0.0.1:1 failed");
        await ssh.DisconnectAsync();
    }

    [SkippableFact]
    public async Task NoShellOption_ConnectsWithoutAShell()
    {
        Skip.If(_sshd.SkipReason is not null, _sshd.SkipReason);
        var connection = Add(_sshd.SshConnection("forward-only", "-N"));

        await using var prepared = await _preparer.PrepareAsync(connection);
        using var ssh = new SshNetProtocol(NullLogger<SshNetProtocol>.Instance, NewVerifier(), _prompt);
        var view = (TerminalView)ssh.CreateView();
        await ssh.ConnectAsync(prepared.Parameters);

        ssh.State.Should().Be(ConnectionState.Connected);
        view.GetScreenText().Should().Contain("No shell was started (-N)");
        await ((ITerminalProtocol)ssh).SendInputAsync("ignored\r"u8.ToArray()); // no shell: input is dropped
        await ssh.DisconnectAsync();
    }

    [SkippableFact]
    public async Task Compression_IsNegotiated_WhenRequested()
    {
        Skip.If(_sshd.SkipReason is not null, _sshd.SkipReason);
        var connector = new SshConnector(NewVerifier(), _prompt, NullLogger.Instance);
        var parameters = new ConnectionParameters
        {
            Hostname = "127.0.0.1",
            Port = TunnelSshdFixture.Port,
            Protocol = ProtocolType.Ssh,
            Username = "root",
            PrivateKeyPath = _sshd.ClientKeyPath,
        };

        using (var plain = await connector.ConnectAsync(parameters, info => new SshClient(info), CancellationToken.None))
            plain.ConnectionInfo.CurrentClientCompressionAlgorithm.Should().Be("none");

        var compressed = parameters with { Extras = new Dictionary<string, string> { [SshExtras.Compression] = "true" } };
        using var client = await connector.ConnectAsync(compressed, info => new SshClient(info), CancellationToken.None);
        client.ConnectionInfo.CurrentClientCompressionAlgorithm.Should().Be("zlib@openssh.com");
        client.RunCommand("echo compressed").Result.Should().Be("compressed\n");
    }

    [SkippableFact]
    public async Task HttpProxy_IsUsedForTheSshConnection()
    {
        Skip.If(_sshd.SkipReason is not null, _sshd.SkipReason);
        var proxyPort = _servers.StartHttpConnectProxy();
        var parameters = new ConnectionParameters
        {
            Hostname = "127.0.0.1",
            Port = TunnelSshdFixture.Port,
            Protocol = ProtocolType.Ssh,
            Username = "root",
            PrivateKeyPath = _sshd.ClientKeyPath,
            Extras = new Dictionary<string, string>
            {
                [SshExtras.ProxyType] = "http",
                [SshExtras.ProxyHost] = "127.0.0.1",
                [SshExtras.ProxyPort] = proxyPort.ToString(),
            },
        };

        using var ssh = new SshNetProtocol(NullLogger<SshNetProtocol>.Instance, NewVerifier(), _prompt);
        var view = (TerminalView)ssh.CreateView();
        await ssh.ConnectAsync(parameters);
        await ((ITerminalProtocol)ssh).SendInputAsync("echo proxied-$((1+1))\r"u8.ToArray());
        await WaitForScreenAsync(view, "proxied-2");
        await ssh.DisconnectAsync();

        _servers.ProxyRequests.Should().ContainSingle().Which.Should().StartWith($"CONNECT 127.0.0.1:{TunnelSshdFixture.Port} HTTP/");
    }
}
