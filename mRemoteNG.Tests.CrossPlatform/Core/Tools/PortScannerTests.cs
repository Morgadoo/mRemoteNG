using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using mRemoteNG.Core.Config.Import;
using ProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;
using mRemoteNG.Core.Tools.PortScanning;
using Xunit;
using static mRemoteNG.Tests.CrossPlatform.Core.Import.ImportTestHelpers;

namespace mRemoteNG.Tests.CrossPlatform.Core.Tools;

public sealed class ScanTargetParserTests
{
    [Theory]
    [InlineData("10.0.0.5", new[] { "10.0.0.5" })]
    [InlineData("10.0.0.1-3", new[] { "10.0.0.1", "10.0.0.2", "10.0.0.3" })]
    [InlineData("10.0.0.254-10.0.1.1", new[] { "10.0.0.254", "10.0.0.255", "10.0.1.0", "10.0.1.1" })]
    [InlineData("192.168.1.8/30", new[] { "192.168.1.9", "192.168.1.10" })]
    [InlineData("192.168.1.8/31", new[] { "192.168.1.8", "192.168.1.9" })]
    [InlineData("server1, 10.0.0.1;server1\n::1", new[] { "server1", "10.0.0.1", "::1" })]
    public void Hosts_ParsesAddressesRangesBlocksAndNames(string text, string[] expected) =>
        HostRangeParser.Parse(text).Should().Equal(expected);

    [Fact]
    public void Hosts_Cidr24_ExcludesNetworkAndBroadcast()
    {
        var hosts = HostRangeParser.Parse("10.1.2.77/24");
        hosts.Should().HaveCount(254);
        hosts[0].Should().Be("10.1.2.1");
        hosts[^1].Should().Be("10.1.2.254");
    }

    [Theory]
    [InlineData("10.0.0.0/8")]
    [InlineData("10.0.0.1/33")]
    [InlineData("10.0.0.1-x")]
    [InlineData("bad_host!")]
    public void Hosts_RejectsInvalidOrHugeInput(string text)
    {
        var act = () => HostRangeParser.Parse(text);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Ports_ParsesListsAndRanges_SortedAndUnique() =>
        PortListParser.Parse("5900-5902, 22 80;22").Should().Equal(22, 80, 5900, 5901, 5902);

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("ssh")]
    public void Ports_RejectsInvalidPorts(string text)
    {
        var act = () => PortListParser.Parse(text);
        act.Should().Throw<FormatException>();
    }
}

/// <summary>Scans loopback listeners that behave like SSH, VNC, HTTP and a silent service.</summary>
public sealed class PortScannerTests : IDisposable
{
    private readonly List<TcpListener> _listeners = [];
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Starts a listener that greets with <paramref name="banner"/> or answers HTTP requests.</summary>
    private int Listen(string? banner, bool http = false)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        var stream = client.GetStream();
                        try
                        {
                            if (banner is not null)
                                await stream.WriteAsync(Encoding.ASCII.GetBytes(banner));
                            var buffer = new byte[256];
                            var read = await stream.ReadAsync(buffer, _stop.Token);
                            if (http && read > 0 && Encoding.ASCII.GetString(buffer, 0, read).StartsWith("HEAD / HTTP/1.0"))
                                await stream.WriteAsync("HTTP/1.0 200 OK\r\nServer: test\r\n\r\n"u8.ToArray());
                        }
                        catch (Exception) { /* client went away */ }
                    }
                });
            }
        });
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static int ClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task Scan_FindsOpenPorts_AndIdentifiesServices()
    {
        var ssh = Listen("SSH-2.0-OpenSSH_9.6\r\n");
        var vnc = Listen("RFB 003.008\n");
        var http = Listen(null, http: true);
        var silent = Listen(null);
        var closed = ClosedPort();
        var scanned = new List<ScanHost>();
        var reports = new List<PortScanProgress>();

        var results = await new PortScanner().ScanAsync(
            ["127.0.0.1", "127.0.0.2"],
            [ssh, vnc, http, silent, closed],
            new PortScanOptions { Timeout = TimeSpan.FromSeconds(2), ResolveHostNames = false },
            host => { lock (scanned) scanned.Add(host); },
            new SynchronousProgress(reports));

        results.Select(r => r.Address).Should().Equal("127.0.0.1", "127.0.0.2");
        scanned.Should().HaveCount(2);
        reports.Should().HaveCount(10).And.Contain(r => r.Completed == 10 && r.HostsCompleted == 2);

        var local = results[0];
        local.OpenPorts.Select(p => p.Port).Should().BeEquivalentTo([ssh, vnc, http, silent]);
        local.OpenPorts.Single(p => p.Port == ssh).Should().Match<OpenPort>(p => p.Service == ScannedService.Ssh && p.Banner == "SSH-2.0-OpenSSH_9.6");
        local.OpenPorts.Single(p => p.Port == vnc).Service.Should().Be(ScannedService.Vnc);
        local.OpenPorts.Single(p => p.Port == http).Service.Should().Be(ScannedService.Http);
        local.OpenPorts.Single(p => p.Port == silent).Service.Should().Be(ScannedService.Unknown);
        local.Ssh.Should().BeTrue();
        local.Vnc.Should().BeTrue();
        local.Rdp.Should().BeFalse();
        local.PortOf(ScannedService.Vnc).Should().Be(vnc);

        results[1].IsReachable.Should().BeFalse("the listeners are bound to 127.0.0.1 only");
    }

    [Fact]
    public async Task Scan_WithoutDetection_UsesWellKnownPorts()
    {
        var port = Listen("RFB 003.008\n");
        var result = await PortScanner.ProbeAsync("127.0.0.1", port, new PortScanOptions { DetectServices = false }, default);
        result!.Service.Should().Be(PortScanner.ServiceForPort(port));
        PortScanner.ServiceForPort(3389).Should().Be(ScannedService.Rdp);
        PortScanner.ServiceForPort(5901).Should().Be(ScannedService.Vnc);
        PortScanner.ServiceForPort(513).Should().Be(ScannedService.Rlogin);
    }

    [Fact]
    public async Task Scan_CanBeCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = () => new PortScanner().ScanAsync(["127.0.0.1"], [ClosedPort()], ct: cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Importer_CreatesConnectionsForHostsWithTheProtocol_OnTheirPort()
    {
        var hosts = new[]
        {
            new ScanHost("10.0.0.5", "desk5.corp.example", [new OpenPort(5901, ScannedService.Vnc, "RFB 003.008", 1), new OpenPort(22, ScannedService.Ssh, null, 1)]),
            new ScanHost("10.0.0.6", "10.0.0.6", [new OpenPort(22, ScannedService.Ssh, null, 1)]),
        };
        var root = NewRoot();

        var vnc = new PortScanImporter(hosts, ProtocolType.VNC).Import("", root);
        var ssh = new PortScanImporter(hosts, ProtocolType.SSH2).Import("", root);

        vnc.ConnectionCount.Should().Be(1);
        vnc.Warnings.Should().ContainSingle().Which.Should().Contain("10.0.0.6");
        var desk = root.Children.Single(c => c.Protocol == ProtocolType.VNC);
        desk.Should().Match<mRemoteNG.Core.Connection.ConnectionInfo>(c =>
            c.Name == "desk5" && c.Hostname == "desk5.corp.example" && c.Port == 5901 && c.Protocol == ProtocolType.VNC);
        ssh.ConnectionCount.Should().Be(2);
        root.Children.Where(c => c.Protocol == ProtocolType.SSH2).Select(c => c.Name).Should().Equal("desk5", "10.0.0.6");
    }

    [Fact]
    public void Importer_RejectsProtocolsAScanCannotFind()
    {
        var act = () => new PortScanImporter([], ProtocolType.PowerShell);
        act.Should().Throw<ArgumentException>();
        PortScanImporter.ServiceFor(ProtocolType.ARD).Should().Be(ScannedService.Vnc);
    }

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var listener in _listeners) listener.Stop();
    }

    private sealed class SynchronousProgress(List<PortScanProgress> reports) : IProgress<PortScanProgress>
    {
        public void Report(PortScanProgress value)
        {
            lock (reports) reports.Add(value);
        }
    }
}
