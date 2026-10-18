using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using mRemoteNG.Avalonia.Controls;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Avalonia.Views.OptionsPages;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Settings;
using mRemoteNG.Core.Tree.Root;
using mRemoteNG.ExternalProviders.Plugins;
using mRemoteNG.PluginContracts;
using mRemoteNG.Protocols.Abstractions;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>Plugins in the app: the connection editor's Plugins page, the tree context menu, Options ▸ Plugins and the wiring.</summary>
public class PluginUiTests
{
    private static readonly ConnectionPropertyDescriptor RdpFlag = ConnectionPropertyDescriptor.ForPlugin(
        "test.rdpFlag", "Test plugin", "RDP flag", "Only for RDP.", ConnectionPropertyEditor.Boolean,
        protocols: [CoreProtocol.RDP]);

    private static readonly ConnectionPropertyDescriptor Alias = ConnectionPropertyDescriptor.ForPlugin(
        "test.alias", "Test plugin", "Alias", "Another name for the host.");

    private static readonly ConnectionPropertyDescriptor Retries = ConnectionPropertyDescriptor.ForPlugin(
        "test.retries", "Other plugin", "Retries", "", ConnectionPropertyEditor.Number, "2");

    private static ConnectionDialogOptions Options => new() { PluginProperties = [RdpFlag, Alias, Retries] };

    [AvaloniaFact]
    public void ConnectionDialog_ShowsPluginProperties_OnAPluginsPage_AndAppliesThem()
    {
        var connection = new ConnectionInfo { Name = "srv", Hostname = "srv.local", Protocol = CoreProtocol.SSH2, Port = 22 };
        connection.SetPluginProperty("test.alias", "old");
        var vm = new ConnectionDialogViewModel(connection, new RootNodeInfo(RootNodeType.Connection), isNew: false, Options);
        var dialog = new ConnectionDialog(vm);
        dialog.Show();
        try
        {
            vm.SelectedPage = vm.Page("plugins");
            Dispatcher.UIThread.RunJobs();
            vm.Page("plugins").IsVisible.Should().BeTrue();
            ShownRows(dialog).Should().Equal("Alias", "Retries");
            dialog.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("h2") && t.IsEffectivelyVisible)
                .Select(t => t.Text).Should().Contain(["Test plugin", "Other plugin"]);

            vm.Field<TextFieldViewModel>("test.alias").Value.Should().Be("old");
            vm.Field<NumberFieldViewModel>("test.retries").IntValue.Should().Be(2);
            vm.Protocol.Value = CoreProtocol.RDP;
            Dispatcher.UIThread.RunJobs();
            ShownRows(dialog).Should().Equal("RDP flag", "Alias", "Retries");

            vm.Field<BoolFieldViewModel>("test.rdpFlag").Value = true;
            vm.Field<TextFieldViewModel>("test.alias").Value = "new";
            vm.Apply().Should().BeTrue();

            connection.PluginProperties.Should().BeEquivalentTo(new Dictionary<string, string>
            {
                ["test.rdpFlag"] = "True", ["test.alias"] = "new",
            }, "the untouched number keeps its default without being stored");
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void ConnectionDialog_HasNoPluginsPage_ForFolders_TheDefaultConnection_OrWithoutPlugins()
    {
        var folder = new ContainerInfo { Name = "f" };
        new ConnectionDialogViewModel(folder, new RootNodeInfo(RootNodeType.Connection), isNew: false, Options)
            .Page("plugins").IsVisible.Should().BeFalse();

        new ConnectionDialogViewModel(new ConnectionInfo(), null, isNew: false,
                new ConnectionDialogOptions { IsDefaultConnection = true, PluginProperties = [Alias] })
            .Page("plugins").IsVisible.Should().BeFalse();

        var plain = new ConnectionDialogViewModel(new ConnectionInfo { Name = "x", Hostname = "h" }, null, isNew: true);
        plain.Page("plugins").IsVisible.Should().BeFalse();
        plain.Fields.Should().NotContain(f => f.Descriptor.IsPluginProperty);
    }

    [AvaloniaFact]
    public void ConnectionDialog_SearchFindsPluginProperties()
    {
        var vm = new ConnectionDialogViewModel(new ConnectionInfo { Name = "x", Hostname = "h", Protocol = CoreProtocol.SSH2, Port = 22 },
            null, isNew: false, Options);

        vm.SearchText = "alias";

        vm.Pages.Where(p => p.IsVisible && !p.IsInheritance).Select(p => p.Key).Should().Equal("plugins");
    }

    private static List<string> ShownRows(Window dialog) =>
        dialog.GetVisualDescendants().OfType<SettingRow>()
            .Where(r => r.Classes.Contains("field-row") && r.IsEffectivelyVisible)
            .Select(r => r.Header ?? "")
            .ToList();

    // ── Host-backed UI ───────────────────────────────────────────────────

    private static PluginHost TestPluginHost(string disabled = "")
    {
        var host = new PluginHost(() => new AppSettings(), defaultDirectories: () => []);
        host.LoadPlugins(typeof(PluginUiTests).Assembly, "tests.dll", PluginHost.ParseIds(disabled));
        return host;
    }

    [AvaloniaFact]
    public void TreeMenu_ListsTheActionsForTheNode_AndRunsThem()
    {
        var host = TestPluginHost();
        var node = new ConnectionInfo { Name = "plugin-web", Hostname = "web.local", Protocol = CoreProtocol.SSH2 };
        var executed = 0;

        var items = PluginTreeMenu.BuildItems(host, node, () => executed++);

        items.Select(i => i.Header).Should().Equal("Copy host");
        items[0].Command!.Execute(null);
        node.GetPluginProperty("test.copied").Should().Be("web.local");
        executed.Should().Be(1);
        PluginTreeMenu.BuildItems(host, new ContainerInfo { Name = "plugin-folder" }).Should().BeEmpty();
        PluginTreeMenu.BuildItems(host, new ConnectionInfo { Name = "other" }).Should().BeEmpty();
    }

    [AvaloniaFact]
    public void TreeContextMenu_InsertsThePluginActions_WhenItOpens()
    {
        var window = TestHost.MainWindow;
        var tree = TestHost.ViewModel.ConnectionTree;
        tree.CreateNewTree();
        tree.AddConnection("plugin-target", CoreProtocol.SSH2, "10.9.8.7");
        Dispatcher.UIThread.RunJobs();
        var host = AppServices.GetRequired<PluginHost>();
        host.LoadPlugins(typeof(PluginUiTests).Assembly, "tests.dll", new HashSet<string>());

        var view = window.GetVisualDescendants().OfType<ConnectionTreeView>().Single();
        var menu = view.FindControl<ContextMenu>("TreeMenu")!;
        view.FindControl<TreeView>("Tree")!.RaiseEvent(new ContextRequestedEventArgs());
        Dispatcher.UIThread.RunJobs();
        menu.IsOpen.Should().BeTrue();
        try
        {
            var item = menu.Items.OfType<MenuItem>().Should().ContainSingle(i => Equals(i.Header, "Copy host")).Subject;
            menu.Items.IndexOf(item).Should().Be(menu.Items.IndexOf(menu.Items.OfType<MenuItem>().Single(i => i.Name == "WakeOnLanMenuItem")) + 2);
            item.Command!.Execute(null);
            tree.SelectedNode!.Model.GetPluginProperty("test.copied").Should().Be("10.9.8.7");
            tree.IsDirty.Should().BeTrue();
        }
        finally
        {
            menu.Close();
        }
    }

    [AvaloniaFact]
    public void Import_AddsThePluginsConnections_AtTheInsertTarget()
    {
        _ = TestHost.MainWindow;
        var tree = TestHost.ViewModel.ConnectionTree;
        tree.CreateNewTree();
        var log = AppServices.GetRequired<LogPanelDockable>();

        PluginIntegration.Import(tree, log,
        [
            new ConnectionInfo { Name = "a", Hostname = "10.0.0.1", Protocol = CoreProtocol.SSH2, Port = 22 },
            new ConnectionInfo { Name = "b", Hostname = "10.0.0.2", Protocol = CoreProtocol.RDP, Port = 3389 },
        ]);

        tree.Root!.Children.Select(c => c.Name).Should().Equal("a", "b");
        tree.SelectedNode!.Name.Should().Be("a");
        log.Entries.Last().Message.Should().Contain("Imported 2 connection(s)");
    }

    [AvaloniaFact]
    public void OptionsPage_ListsThePlugins_AndDisablingOneIsSaved()
    {
        _ = TestHost.MainWindow;
        var working = new AppSettings();
        var page = new PluginsSettingsViewModel(working, TestPluginHost());

        page.HasPlugins.Should().BeTrue();
        var action = page.Entries.Single(e => e.Id == CopyHostPlugin.PluginId);
        action.StateText.Should().Be("Loaded");
        action.Details.Should().Be("1.2.0 · Tree action");
        page.Entries.Single(e => e.Id == NeedsNewerAppPlugin.PluginId).Should().Match<PluginEntryViewModel>(e =>
            !e.CanToggle && e.IsProblem && e.StateText == "Needs a newer version");

        action.IsEnabled = false;
        working.DisabledPlugins.Should().Be(CopyHostPlugin.PluginId);
        action.IsEnabled = true;
        working.DisabledPlugins.Should().BeEmpty();

        page.PluginFolderPaths = "  /opt/plugins ; ~/more  ";
        working.PluginFolderPaths.Should().Be("/opt/plugins ; ~/more");
    }

    [AvaloniaFact]
    public void OptionsWindow_HasAPluginsPage_AndTheContainerRegistersTheHost()
    {
        _ = TestHost.MainWindow;
        AppServices.GetRequired<PluginHost>().IsLoaded.Should().BeTrue("plugins load when the app starts");
        AppServices.Provider.GetServices<IConnectionPreparationStep>().OfType<PluginAddressPreparationStep>()
            .Should().ContainSingle().Which.Order.Should().Be(90);

        var vm = AppServices.GetRequired<OptionsWindowViewModel>();
        var window = new OptionsWindow { DataContext = vm };
        window.Show();
        try
        {
            vm.SelectedCategory = vm.Categories.Single(c => c.Key == "plugins");
            Dispatcher.UIThread.RunJobs();
            window.GetVisualDescendants().OfType<PluginsSettingsPage>().Should().ContainSingle();
            vm.CurrentPage.Should().BeSameAs(vm.Plugins);
        }
        finally
        {
            window.Close();
        }
    }

    public sealed class CopyHostPlugin : ITreeContextActionPlugin
    {
        public const string PluginId = "test.copyHost";
        public string Id => PluginId;
        public string DisplayName => "Copy host";
        public Version Version => new(1, 2, 0);
        public Version MinimumHostVersion => new(1, 0);
        public TreeContextMenuAction TreeContextMenuAction { get; } = new() { MenuText = "Copy host" };
        public void Initialize(IPluginContext context) { }

        // The tree test loads this plugin into the shared app host, so it only acts on its own nodes.
        public bool CanExecute(IPluginConnection connection) =>
            !connection.IsContainer && connection.Name.StartsWith("plugin-", StringComparison.Ordinal);

        public void Execute(IPluginConnection connection) =>
            connection.SetPluginProperty("test.copied", connection.Hostname);
    }

    public sealed class NeedsNewerAppPlugin : IPlugin
    {
        public const string PluginId = "test.newer";
        public string Id => PluginId;
        public string DisplayName => "Newer";
        public Version Version => new(1, 0);
        public Version MinimumHostVersion => new(999, 0);
        public void Initialize(IPluginContext context) { }
    }
}
