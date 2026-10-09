using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using FluentAssertions;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Config.Import.ActiveDirectory;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Vnc;
using Xunit;
using CoreProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>Port scanner, UltraVNC SingleClick listener and Active Directory import UI, driven headlessly.</summary>
public class VncToolsTests
{
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }
        Dispatcher.UIThread.RunJobs();
        condition().Should().BeTrue(because);
    }

    // ── Port scanner ───────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task PortScanner_FindsAService_AndImportsItIntoTheTree()
    {
        _ = TestHost.MainWindow;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token);
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("SSH-2.0-UiTest\r\n"));
                await Task.Delay(500);
            }
        });

        try
        {
            var dialog = new PortScannerDialog();
            dialog.Show();
            var vm = (PortScannerViewModel)dialog.DataContext!;
            vm.Hosts = "127.0.0.1";
            vm.Ports = port.ToString();
            vm.ResolveHostNames = false;

            var scan = vm.StartScanAsync();
            await WaitUntilAsync(() => scan.IsCompleted, "the scan finishes");
            await scan;

            vm.Results.Should().ContainSingle().Which.Ssh.Should().Be("✓");
            vm.StatusText.Should().Contain("1 with open ports");

            TestHost.ViewModel.ConnectionTree.SelectedNode = null;
            vm.ImportProtocol = CoreProtocolType.SSH2;
            vm.ImportSelected();

            vm.StatusText.Should().StartWith("Imported 1 SSH2 connection");
            var imported = ((mRemoteNG.Core.Container.ContainerInfo)TestHost.ViewModel.ConnectionTree.Nodes[0].Model).GetRecursiveChildList()
                .Where(c => c.Hostname == "127.0.0.1" && c.Port == port).ToList();
            imported.Should().ContainSingle().Which.Protocol.Should().Be(CoreProtocolType.SSH2);
            TestHost.ViewModel.ConnectionTree.IsDirty.Should().BeTrue();
            dialog.Close();
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
        }
    }

    [AvaloniaFact]
    public async Task PortScanner_InvalidRange_IsExplained()
    {
        var vm = new PortScannerViewModel();
        vm.Hosts = "10.0.0.0/8";
        await vm.StartScanAsync();
        vm.StatusText.Should().Contain("more than");
        vm.Results.Should().BeEmpty();
    }

    // ── UltraVNC SingleClick listener ──────────────────────────────────────

    [AvaloniaFact]
    public async Task SingleClickListener_OpensASessionTab_ForAnIncomingServer()
    {
        _ = TestHost.MainWindow;
        var sessions = TestHost.ViewModel.Sessions;
        var before = sessions.Sessions.Count;
        var vm = UltraVncListenerViewModel.CreateForApp();
        vm.Port = 0;
        var window = new UltraVncListenerWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        vm.IsListening.Should().BeTrue();

        // The "VNC server" dials the listening viewer and speaks RFB 3.8 with no authentication.
        using var server = new TcpClient();
        await server.ConnectAsync(IPAddress.Loopback, vm.ListeningPort);
        var stream = server.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes("RFB 003.008\n"));
        await stream.ReadExactlyAsync(new byte[12]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await stream.WriteAsync(new byte[] { 1, 1 });
        await stream.ReadExactlyAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        byte[] serverInit =
        [
            0, 0, 0, 0, // SecurityResult OK
            0, 32, 0, 24, 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0,
            0, 0, 0, 5, .. "desk1"u8.ToArray(),
        ];
        await stream.WriteAsync(serverInit);

        await WaitUntilAsync(() => vm.Connections.Count == 1, "the connection is listed");
        vm.Connections[0].Status.Should().Be("Connected: \"desk1\"");
        sessions.Sessions.Should().HaveCount(before + 1);
        var tab = sessions.ActiveSession!;
        tab.Protocol.Should().BeOfType<VncProtocol>().Which.State.Should().Be(ConnectionState.Connected);
        tab.Hostname.Should().Be("127.0.0.1");

        window.Close();
        await WaitUntilAsync(() => !vm.IsListening, "closing the window stops listening");
        await sessions.CloseSessionAsync(tab);
    }

    // ── Active Directory import ────────────────────────────────────────────

    private sealed class FakeDirectory : IDirectoryBrowser
    {
        public LdapServerSettings? Settings { get; init; }
        public void Bind() { }
        public string GetDefaultNamingContext() => "DC=corp,DC=example";

        public IReadOnlyList<DirectoryContainer> GetChildContainers(string dn) => dn switch
        {
            "DC=corp,DC=example" => [new("OU=Servers,DC=corp,DC=example", "Servers", true)],
            "OU=Servers,DC=corp,DC=example" => [new("OU=Web,OU=Servers,DC=corp,DC=example", "Web", true)],
            _ => [],
        };

        public IReadOnlyList<DirectoryComputer> GetComputers(string dn, bool includeSubtree) =>
            dn == "OU=Servers,DC=corp,DC=example"
                ? includeSubtree
                    ? [new("CN=DB01,OU=Servers,DC=corp,DC=example", "DB01", "db01.corp.example", null, "Windows Server 2019"),
                       new("CN=WEB01,OU=Web,OU=Servers,DC=corp,DC=example", "WEB01", "web01.corp.example", "Web", null)]
                    : [new("CN=DB01,OU=Servers,DC=corp,DC=example", "DB01", "db01.corp.example", null, "Windows Server 2019")]
                : [];

        public void Dispose() { }
    }

    [AvaloniaFact]
    public async Task ActiveDirectoryDialog_BrowsesOus_AndBuildsTheRequest()
    {
        LdapServerSettings? used = null;
        var vm = new ActiveDirectoryImportViewModel(settings =>
        {
            used = settings;
            return new FakeDirectory();
        })
        {
            Server = "dc1",
            BindMode = LdapBindMode.Simple,
            Username = "CN=admin",
            Password = "pw",
        };
        var dialog = new ActiveDirectoryImportDialog(vm);
        dialog.Show();

        await vm.ConnectAsync();
        used!.Server.Should().Be("dc1");
        vm.Nodes.Should().ContainSingle().Which.Children.Select(c => c.Name).Should().Equal("Servers");

        vm.SelectedNode = vm.Nodes[0].Children[0];
        await WaitUntilAsync(() => vm.Computers.Count == 2, "sub-OUs are included by default");
        vm.Computers.Select(c => c.Location).Should().Equal("Servers", "Servers / Web");

        vm.Computers[0].IsSelected = false;
        var request = vm.BuildRequest()!;
        request.BaseDn.Should().Be("OU=Servers,DC=corp,DC=example");
        request.IncludeSubOus.Should().BeTrue();
        request.SelectedComputers.Should().Equal("CN=WEB01,OU=Web,OU=Servers,DC=corp,DC=example");
        request.Server.Password.Should().Be("pw");
        dialog.Close();
    }

    [AvaloniaFact]
    public void ImportDialog_OffersActiveDirectory()
    {
        ImportSourceDescriptor.For(ImportSourceType.ActiveDirectory).SourceIsDirectory.Should().BeTrue();
        var dialog = new ImportDialog(null);
        dialog.Show();
        dialog.FindControl<global::Avalonia.Controls.ListBox>("SourceTypeBox")!.SelectedIndex = ImportSourceDescriptor.All.ToList().FindIndex(d => d.Type == ImportSourceType.ActiveDirectory);
        Dispatcher.UIThread.RunJobs();
        dialog.FindControl<global::Avalonia.Controls.TextBox>("FilePathBox")!.IsReadOnly.Should().BeTrue();
        dialog.FindControl<global::Avalonia.Controls.Button>("BrowseButton")!.Content.Should().Be("Browse directory...");
        dialog.Close();
    }
}
