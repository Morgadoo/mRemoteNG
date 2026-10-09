using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Rdp;
using Xunit;
using Keys = mRemoteNG.Protocols.Abstractions.ConnectionParametersFactory.Keys;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Rdp;

/// <summary>
/// Drives the real <see cref="RdpProtocol"/> (FreeRDP in its own window, no Avalonia view) against a live server.
///
/// Requirements (otherwise the tests are skipped):
///   • Linux with xfreerdp3 (FreeRDP 3) on PATH, and an X display: $DISPLAY, or Xvfb on PATH (one is started);
///   • RDP_TEST_HOST, RDP_TEST_USER, RDP_TEST_PASS (optional RDP_TEST_PORT, default 3389).
///   • RDP_TEST_NLA=true when the server authenticates with NLA/CredSSP (Windows): enables the wrong-password
///     test. xrdp has no NLA — it accepts the RDP connection and reports a bad password inside its own login
///     screen — so a protocol-level authentication failure cannot be observed against it.
///   For xrdp set RDP_TEST_NLA=false (or leave unset): NLA is then switched off for the connection.
///
/// Run with: dotnet test --filter "Category=Integration&FullyQualifiedName~Rdp"
/// </summary>
[Trait("Category", "Integration")]
public sealed class RdpIntegrationTests : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);

    private readonly string? _host = Environment.GetEnvironmentVariable("RDP_TEST_HOST");
    private readonly string? _user = Environment.GetEnvironmentVariable("RDP_TEST_USER");
    private readonly string? _pass = Environment.GetEnvironmentVariable("RDP_TEST_PASS");
    private readonly int _port = int.TryParse(Environment.GetEnvironmentVariable("RDP_TEST_PORT"), out int p) ? p : 3389;
    private readonly bool _nla = string.Equals(Environment.GetEnvironmentVariable("RDP_TEST_NLA"), "true", StringComparison.OrdinalIgnoreCase);
    private Process? _xvfb;

    private void SkipUnlessRunnable(bool needsServer = true)
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "RDP integration tests run on Linux/X11 only");
        Skip.If(FindOnPath("xfreerdp3") is null, "xfreerdp3 (FreeRDP 3) is not installed");
        if (needsServer)
            Skip.If(string.IsNullOrEmpty(_host) || string.IsNullOrEmpty(_user), "RDP_TEST_HOST / RDP_TEST_USER not set");
        EnsureDisplay();
    }

    private ConnectionParameters Parameters(string? password = null, string? host = null, int? port = null) => new()
    {
        Hostname = host ?? _host ?? "127.0.0.1",
        Port = port ?? _port,
        Protocol = ProtocolType.Rdp,
        Username = _user,
        Password = password ?? _pass,
        Extras = new Dictionary<string, string>
        {
            [Keys.RdpNla] = _nla ? "true" : "false",
            [Keys.RdpSound] = "off",
            // Do not pin the test server's certificate in the user's FreeRDP store.
            [RdpProtocol.CertPolicyKey] = "ignore",
        },
    };

    [SkippableFact]
    public async Task Connect_ValidCredentials_IsConnectedThenDisconnects()
    {
        SkipUnlessRunnable();
        using var protocol = new RdpProtocol(NullLogger<RdpProtocol>.Instance);
        using var timeout = new CancellationTokenSource(ConnectTimeout);
        await protocol.ConnectAsync(Parameters(), timeout.Token);

        protocol.State.Should().Be(ConnectionState.Connected);
        protocol.IsEmbedded.Should().BeFalse("no view was created, so FreeRDP runs in its own window");
        Process.GetProcessesByName("xfreerdp3").Should().NotBeEmpty();

        await protocol.DisconnectAsync();

        protocol.State.Should().Be(ConnectionState.Disconnected);
    }

    [SkippableFact]
    public async Task Connect_ClosedPort_ReportsErrorWithReason()
    {
        SkipUnlessRunnable(needsServer: false);
        int closedPort = GetUnusedTcpPort();
        using var protocol = new RdpProtocol(NullLogger<RdpProtocol>.Instance);

        using var timeout = new CancellationTokenSource(ConnectTimeout);
        var act = () => protocol.ConnectAsync(Parameters(host: "127.0.0.1", port: closedPort), timeout.Token);

        (await act.Should().ThrowAsync<RdpConnectionException>())
            .Which.Message.Should().StartWith("Could not connect to the host");
        protocol.State.Should().Be(ConnectionState.Error);
    }

    [SkippableFact]
    public async Task Connect_WrongPassword_WithNla_ReportsAuthenticationFailure()
    {
        SkipUnlessRunnable();
        Skip.IfNot(_nla, "RDP_TEST_NLA is not true: the server does not authenticate at the protocol level (e.g. xrdp)");
        using var protocol = new RdpProtocol(NullLogger<RdpProtocol>.Instance);

        using var timeout = new CancellationTokenSource(ConnectTimeout);
        var act = () => protocol.ConnectAsync(Parameters(password: "definitely-not-the-password"), timeout.Token);

        (await act.Should().ThrowAsync<RdpConnectionException>())
            .Which.Message.Should().StartWith("Authentication failed");
        protocol.State.Should().Be(ConnectionState.Error);
    }

    private void EnsureDisplay()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return;
        string? xvfb = FindOnPath("Xvfb");
        Skip.If(xvfb is null, "No DISPLAY and Xvfb is not installed");

        for (int display = 180; display < 200; display++)
        {
            if (File.Exists($"/tmp/.X11-unix/X{display}"))
                continue;
            var psi = new ProcessStartInfo(xvfb!, $":{display} -screen 0 1280x800x24 -nolisten tcp")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            _xvfb = Process.Start(psi);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !File.Exists($"/tmp/.X11-unix/X{display}") && _xvfb is { HasExited: false })
                Thread.Sleep(100);
            if (File.Exists($"/tmp/.X11-unix/X{display}"))
            {
                // Inherited by FreeRDP. DISPLAY was unset, so nothing else in this process relied on it.
                Environment.SetEnvironmentVariable("DISPLAY", $":{display}");
                return;
            }
            _xvfb?.Dispose();
            _xvfb = null;
        }
        Skip.If(true, "Could not start Xvfb");
    }

    private static int GetUnusedTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, exe))
            .FirstOrDefault(File.Exists);

    public void Dispose()
    {
        if (_xvfb is null) return;
        try
        {
            if (!_xvfb.HasExited) _xvfb.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        _xvfb.Dispose();
        Environment.SetEnvironmentVariable("DISPLAY", null);
    }
}
