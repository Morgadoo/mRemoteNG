using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Settings;
using mRemoteNG.Protocols.Abstractions;
using Xunit;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>
/// End-to-end checks of the real main window, view-models and DI container, rendered headlessly.
/// One shared window: these tests run sequentially on the Avalonia UI thread.
/// </summary>
public class MainWindowTests
{
    private static ConnectionTreeViewModel Tree => TestHost.ViewModel.ConnectionTree;

    private static IEnumerable<ConnectionNodeViewModel> AllNodes() =>
        Tree.Nodes.SelectMany(n => n.SelfAndDescendants());

    [AvaloniaFact]
    public void MainWindow_Starts_WithEmptyConnectionsRoot()
    {
        var window = TestHost.MainWindow;

        window.IsVisible.Should().BeTrue();
        window.GetVisualDescendants().OfType<TreeView>().Should().ContainSingle();
        Tree.Nodes.Should().ContainSingle().Which.IsRoot.Should().BeTrue();
    }

    [AvaloniaFact]
    public void LoadingLegacyFile_ShowsItsFoldersInTheTreeView()
    {
        _ = TestHost.MainWindow;
        Tree.LoadFromFile(TestHost.Fixture("confCons_v2_6.xml"));
        Dispatcher.UIThread.RunJobs();

        var root = Tree.Nodes.Should().ContainSingle().Subject;
        root.Children.Select(c => c.Name).Should().Contain(["Folder1", "Folder2", "rootConnection"]);

        var treeView = TestHost.MainWindow.GetVisualDescendants().OfType<TreeView>().Single();
        var realized = treeView.GetVisualDescendants().OfType<TreeViewItem>()
            .Select(item => item.DataContext).OfType<ConnectionNodeViewModel>().Select(n => n.Name);
        realized.Should().Contain("Folder1", "the tree view renders the loaded model");
        Tree.IsDirty.Should().BeFalse();
    }

    [AvaloniaFact]
    public void LoadingProtectedFile_RequiresTheMasterPassword()
    {
        _ = TestHost.MainWindow;
        var path = TestHost.Fixture("confCons_v2_6_passwordis_Password.xml");

        var withoutPassword = () => Tree.LoadFromFile(path);
        withoutPassword.Should().Throw<ConnectionFilePasswordException>();

        Tree.LoadFromFile(path, "Password");
        AllNodes().Should().Contain(n => n.Name == "rootConnection");
    }

    [AvaloniaFact]
    public void ModelChanges_ShowUpInTheTree_AndMarkTheFileUnsaved()
    {
        _ = TestHost.MainWindow;
        Tree.LoadFromFile(TestHost.Fixture("confCons_v2_6.xml"));
        var node = AllNodes().First(n => n.Name == "Connection1.1");

        node.Model.Name = "Renamed in model";
        Tree.MarkDirty();
        Dispatcher.UIThread.RunJobs();

        node.Name.Should().Be("Renamed in model");
        Tree.IsDirty.Should().BeTrue();
        TestHost.MainWindow.Title.Should().Contain("*");
    }

    [AvaloniaFact]
    public void Search_KeepsMatchesAndTheirFoldersVisible()
    {
        _ = TestHost.MainWindow;
        Tree.LoadFromFile(TestHost.Fixture("confCons_v2_6.xml"));

        Tree.SearchFilter = "Connection2.1.1";
        Dispatcher.UIThread.RunJobs();

        AllNodes().Single(n => n.Name == "Connection2.1.1").IsVisible.Should().BeTrue();
        AllNodes().Single(n => n.Name == "Folder2.1").IsVisible.Should().BeTrue("ancestors of a match stay visible");
        AllNodes().Single(n => n.Name == "Connection1.1").IsVisible.Should().BeFalse();

        Tree.SearchFilter = string.Empty;
        Dispatcher.UIThread.RunJobs();
        AllNodes().Should().OnlyContain(n => n.IsVisible);
    }

    [AvaloniaFact]
    public void SwitchingTheme_ReplacesThePaletteInsteadOfStackingIt()
    {
        var window = TestHost.MainWindow;
        try
        {
            ThemeService.Instance.Apply(ThemeMode.Light);
            Dispatcher.UIThread.RunJobs();

            PaletteIncludes().Should().ContainSingle().Which.Should().EndWith("LightTheme.axaml");
            // A palette inlined by the XAML compiler is not a StyleInclude; count anything defining it.
            Application.Current!.Styles.Count(st => (st as IResourceProvider)?.TryGetResource("AppBg0Brush", null, out _) == true)
                .Should().Be(1, "exactly one palette may be active");
            window.TryFindResource("AppBg0Brush", out var light).Should().BeTrue();
            ((ISolidColorBrush)light!).Color.Should().Be(Color.Parse("#f5f5f5"));
            Application.Current!.ActualThemeVariant.Should().Be(ThemeVariant.Light);
        }
        finally
        {
            ThemeService.Instance.Apply(ThemeMode.Dark);
            Dispatcher.UIThread.RunJobs();
        }

        PaletteIncludes().Should().ContainSingle().Which.Should().EndWith("DarkTheme.axaml");
    }

    private static List<string> PaletteIncludes() =>
        Application.Current!.Styles.OfType<global::Avalonia.Markup.Xaml.Styling.StyleInclude>()
            .Select(s => s.Source?.ToString() ?? "")
            .Where(s => s.Contains("/Themes/", StringComparison.Ordinal))
            .ToList();

    [AvaloniaFact]
    public async Task SessionTab_OpensForAConnection_AndClosingRemovesIt()
    {
        _ = TestHost.MainWindow;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        var sessions = TestHost.ViewModel.Sessions;
        var before = sessions.Sessions.Count;

        try
        {
            await sessions.OpenConnectionAsync(
                new ConnectionParameters { Hostname = "127.0.0.1", Port = port, Protocol = ProtocolType.Raw },
                AppServices.GetRequired<IProtocolFactory>());
            using var server = await accept.WaitAsync(TimeSpan.FromSeconds(5));

            sessions.Sessions.Should().HaveCount(before + 1);
            var tab = sessions.ActiveSession!;
            tab.Hostname.Should().Be("127.0.0.1");

            tab.CloseCommand.Execute().Subscribe();
            for (var i = 0; i < 50 && sessions.Sessions.Contains(tab); i++)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }

            sessions.Sessions.Should().NotContain(tab, "closing a tab removes and disposes it");
        }
        finally
        {
            listener.Stop();
        }
    }
}
