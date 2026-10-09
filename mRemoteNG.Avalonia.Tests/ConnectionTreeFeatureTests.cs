using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Settings;
using mRemoteNG.Core.Tree.Root;
using mRemoteNG.Platform;
using mRemoteNG.Protocols.Abstractions;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>Connection editor, tree commands, PuTTY root and file security, driven through the real views.</summary>
public class ConnectionTreeFeatureTests
{
    private static ConnectionTreeViewModel Tree => TestHost.ViewModel.ConnectionTree;

    private static IEnumerable<ConnectionNodeViewModel> AllNodes() => Tree.Nodes.SelectMany(n => n.SelfAndDescendants());

    private static ConnectionNodeViewModel Node(string name) => AllNodes().First(n => n.Name == name);

    private static string CopyFixture(string name)
    {
        var path = Path.Combine(TestHost.ConfigDirectory, $"{Guid.NewGuid():N}-{name}");
        File.Copy(TestHost.Fixture(name), path);
        return path;
    }

    // ── Connection dialog ─────────────────────────────────────────────────

    [AvaloniaFact]
    public void ConnectionDialog_ShowsOnlyTheSectionsOfTheChosenProtocol()
    {
        _ = TestHost.MainWindow;
        var connection = ConnectionDefaults.ApplyNewConnectionDefaults(new ConnectionInfo { Name = "srv", Hostname = "srv.local" });
        connection.Protocol = CoreProtocol.RDP;
        connection.Port = 3389;
        var vm = new ConnectionDialogViewModel(connection, new RootNodeInfo(RootNodeType.Connection), isNew: false);
        var dialog = new ConnectionDialog(vm);
        dialog.Show();
        try
        {
            PageItem(dialog, "Display").IsVisible.Should().BeTrue();
            VisibleLabels(dialog, vm, "display").Should().Contain("Resolution").And.NotContain("Proxy type");
            VisibleLabels(dialog, vm, "redirection").Should().Contain("Disk drives").And.NotContain("Sound quality");
            VisibleLabels(dialog, vm, "gateway").Should().Contain("Use gateway").And.NotContain("Gateway host name");

            vm.Field<ChoiceFieldViewModel>(nameof(ConnectionInfo.RDGatewayUsageMethod)).Value = RDGatewayUsageMethod.Always;
            vm.Field<ChoiceFieldViewModel>(nameof(ConnectionInfo.RedirectSound)).Value = RDPSounds.BringToThisComputer;
            Dispatcher.UIThread.RunJobs();
            VisibleLabels(dialog, vm, "gateway").Should().Contain("Gateway host name");
            VisibleLabels(dialog, vm, "redirection").Should().Contain("Sound quality");

            vm.Protocol.Value = CoreProtocol.SSH2;
            Dispatcher.UIThread.RunJobs();
            vm.Port.IntValue.Should().Be(22, "the default port follows the protocol");
            PageItem(dialog, "Display").IsVisible.Should().BeFalse("SSH has no remote desktop display settings");
            PageItem(dialog, "Gateway").IsVisible.Should().BeFalse();
            vm.Field(nameof(ConnectionInfo.PuttySession)).IsVisible.Should().BeTrue();
            vm.Field(nameof(ConnectionInfo.Resolution)).IsVisible.Should().BeFalse();
            VisibleLabels(dialog, vm, "protocol").Should().Contain("PuTTY session");

            vm.Protocol.Value = CoreProtocol.VNC;
            Dispatcher.UIThread.RunJobs();
            VisibleLabels(dialog, vm, "protocol").Should().Contain(["View only", "Proxy type", "Encoding"]).And.NotContain("Resolution");

            // The search box filters the properties and the pages.
            vm.SearchText = "encoding";
            Dispatcher.UIThread.RunJobs();
            vm.Pages.Where(p => p.IsVisible).Select(p => p.Key).Should().Equal("protocol", "inheritance");
            vm.SelectedPage!.Key.Should().Be("protocol");
            VisibleLabels(dialog, vm, "protocol").Should().Equal("Encoding");
            vm.SearchText = string.Empty;
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void ConnectionDialog_InheritedValuesComeFromTheFolder_AndApplyWritesFlags()
    {
        _ = TestHost.MainWindow;
        var root = new RootNodeInfo(RootNodeType.Connection);
        var folder = new ContainerInfo { Name = "Team", Username = "team-user", Panel = "Team panel" };
        var connection = new ConnectionInfo { Name = "srv", Hostname = "srv.local", Protocol = CoreProtocol.SSH2, Port = 22, Username = "own-user" };
        root.AddChild(folder);
        folder.AddChild(connection);
        connection.Inheritance.Username = true;

        var vm = new ConnectionDialogViewModel(connection, folder, isNew: false);
        var dialog = new ConnectionDialog(vm);
        dialog.Show();
        try
        {
            var username = vm.Field<TextFieldViewModel>(nameof(ConnectionInfo.Username));
            username.Value.Should().Be("team-user");
            username.IsEditable.Should().BeFalse();

            vm.SelectedPage = vm.Page("credentials");
            Dispatcher.UIThread.RunJobs();
            var row = Row(dialog, "Username");
            var inherit = row.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Classes.Contains("inherit"));
            inherit.IsChecked.Should().BeTrue();
            ToolTip.GetTip(inherit).Should().Be("Inherit from folder \"Team\"");
            row.GetVisualDescendants().OfType<TextBox>().Single().IsEffectivelyEnabled.Should().BeFalse();

            username.Inherit = false;
            username.Value.Should().Be("own-user", "the node's own value comes back");
            username.Value = "edited";

            vm.InheritEverything.Should().BeFalse();
            vm.InheritEverything = true;
            vm.Fields.Where(f => f.CanInherit).Should().OnlyContain(f => f.Inherit);
            vm.Field<TextFieldViewModel>(nameof(ConnectionInfo.Panel)).Value.Should().Be("Team panel");

            vm.Apply().Should().BeTrue();
            connection.Inheritance.EverythingInherited.Should().BeTrue();
            connection.Username.Should().Be("team-user");
            connection.Inheritance.Username = false;
            connection.Username.Should().Be("edited", "the own value typed before inheriting is kept");
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void ConnectionDialog_ValidatesMacAddressAndRequiredFields()
    {
        var vm = new ConnectionDialogViewModel(new ConnectionInfo { Name = "x", Hostname = "h", Protocol = CoreProtocol.SSH2, Port = 22 },
            null, isNew: true);
        vm.IsValid.Should().BeTrue();

        vm.Field<TextFieldViewModel>(nameof(ConnectionInfo.MacAddress)).Value = "not-a-mac";
        vm.IsValid.Should().BeFalse();
        vm.Field<TextFieldViewModel>(nameof(ConnectionInfo.MacAddress)).Value = "00:11:22:33:44:55";
        vm.Name = " ";
        vm.NameError.Should().NotBeNull();
        vm.IsValid.Should().BeFalse();
    }

    // ── Tree commands ─────────────────────────────────────────────────────

    [AvaloniaFact]
    public void F2_RenamesInPlace_EnterCommits_EscapeCancels()
    {
        var window = TestHost.MainWindow;
        Tree.LoadFromFile(TestHost.Fixture("confCons_v2_6.xml"));
        Dispatcher.UIThread.RunJobs();
        var node = Node("Connection1.1");
        Tree.SelectedNode = node;
        Dispatcher.UIThread.RunJobs();
        var treeView = window.GetVisualDescendants().OfType<TreeView>().Single();
        treeView.GetVisualDescendants().OfType<TreeViewItem>().First(i => ReferenceEquals(i.DataContext, node)).Focus();
        Dispatcher.UIThread.RunJobs();

        window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
        Dispatcher.UIThread.RunJobs();
        node.IsEditing.Should().BeTrue();
        var box = treeView.GetVisualDescendants().OfType<TextBox>().Single(t => t.Classes.Contains("rename") && t.IsVisible);
        box.IsFocused.Should().BeTrue("the editor takes focus with the name selected");

        window.KeyTextInput("Web server");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();
        node.IsEditing.Should().BeFalse();
        node.Model.Name.Should().Be("Web server");
        Tree.IsDirty.Should().BeTrue();

        Tree.BeginRename(node);
        node.EditName = "discarded";
        Tree.CancelRename(node);
        node.Model.Name.Should().Be("Web server");
    }

    [AvaloniaFact]
    public void SortExpandCollapseAndInheritanceCommands_WorkOnTheSelection()
    {
        _ = TestHost.MainWindow;
        Tree.LoadFromFile(TestHost.Fixture("confCons_v2_6.xml"));
        Tree.SelectedNode = Tree.Nodes[0];

        Tree.SortDescendingCommand.Execute().Subscribe();
        Tree.Root!.Children.Select(c => c.Name).Should().Equal("rootConnection", "Folder2", "Folder1");
        Tree.SortAscendingCommand.Execute().Subscribe();
        Tree.Root.Children.Select(c => c.Name).Should().Equal("Folder1", "Folder2", "rootConnection");

        Tree.CollapseAllCommand.Execute().Subscribe();
        Tree.Root.GetRecursiveChildList().OfType<ContainerInfo>().Should().OnlyContain(f => !f.IsExpanded);
        Tree.Root.IsExpanded.Should().BeTrue("the root stays open");
        Tree.ExpandAllCommand.Execute().Subscribe();
        Tree.Root.GetRecursiveChildList().OfType<ContainerInfo>().Should().OnlyContain(f => f.IsExpanded);

        var folder = Node("Folder2");
        folder.Model.Inheritance.Domain = true;
        Tree.SelectedNode = folder;
        Tree.ApplyInheritanceToChildrenCommand.Execute().Subscribe();
        ((ContainerInfo)folder.Model).GetRecursiveChildList().Should().OnlyContain(n => n.Inheritance.Domain);
    }

    [AvaloniaFact]
    public async Task CopyHostname_PutsTheHostOnTheClipboard()
    {
        var window = TestHost.MainWindow;
        Tree.CreateNewTree();
        Tree.AddConnection("web", CoreProtocol.HTTPS, "web.example.org");

        await Tree.CopyHostnameCommand.Execute();

        (await window.Clipboard!.GetTextAsync()).Should().Be("web.example.org");
    }

    [AvaloniaFact]
    public async Task ConnectAndDisconnect_OpenAndCloseTheNodesSessions()
    {
        _ = TestHost.MainWindow;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var sessions = TestHost.ViewModel.Sessions;
        try
        {
            Tree.CreateNewTree();
            var folder = new ContainerInfo { Name = "raw" };
            Tree.Root!.AddChild(folder);
            var node = Tree.AddConnection(new ConnectionInfo { Name = "socket", Protocol = CoreProtocol.RAW, Hostname = "127.0.0.1", Port = port }, folder)!;
            var accept = listener.AcceptTcpClientAsync();

            Tree.SelectedNode = Tree.FindNode(folder);
            await Tree.ConnectSelectedCommand.Execute(); // a folder connects everything inside
            using var server = await accept.WaitAsync(TimeSpan.FromSeconds(5));
            Tree.SessionsOf(node.Model).Should().ContainSingle().Which.Connection.Should().BeSameAs(node.Model);

            await Tree.DisconnectCommand.Execute();
            Tree.SessionsOf(node.Model).Should().BeEmpty();
            sessions.Sessions.Should().NotContain(s => s.Connection == node.Model);
        }
        finally
        {
            listener.Stop();
        }
    }

    [AvaloniaFact]
    public async Task ConnectWithOptions_PassesTheChosenOptions()
    {
        _ = TestHost.MainWindow;
        Tree.CreateNewTree();
        Tree.AddConnection("rdp", CoreProtocol.RDP, "rdp.invalid");
        ConnectWithOptionsViewModel? shown = null;
        using var handler = Tree.ChooseConnectOptions.RegisterHandler(context =>
        {
            shown = context.Input;
            context.Input.NoCredentials = true;
            context.Input.ConsoleSession = ConsoleSessionChoice.NoConsole;
            context.Input.Panel = "Ops";
            context.SetOutput(false); // cancel: nothing connects
        });

        await Tree.ConnectWithOptionsCommand.Execute();

        shown.Should().NotBeNull();
        shown!.IsRdp.Should().BeTrue();
        shown.ToOptions().Should().Be(new ConnectOptions { NoCredentials = true, ConsoleSession = false, Panel = "Ops" });
        TestHost.ViewModel.Sessions.Sessions.Should().NotContain(s => s.Connection != null && s.Connection.Name == "rdp");
    }

    [Theory]
    [InlineData(ConnectPreset.NoCredentials)]
    [InlineData(ConnectPreset.ConsoleSession)]
    [InlineData(ConnectPreset.NoConsoleSession)]
    [InlineData(ConnectPreset.Fullscreen)]
    [InlineData(ConnectPreset.ViewOnly)]
    public void ConnectPresets_MapToConnectOptions(ConnectPreset preset)
    {
        var expected = preset switch
        {
            ConnectPreset.NoCredentials => new ConnectOptions { NoCredentials = true },
            ConnectPreset.ConsoleSession => new ConnectOptions { ConsoleSession = true },
            ConnectPreset.NoConsoleSession => new ConnectOptions { ConsoleSession = false },
            ConnectPreset.Fullscreen => new ConnectOptions { Fullscreen = true },
            _ => new ConnectOptions { ViewOnly = true },
        };

        ConnectWithOptionsViewModel.ForPreset(preset).Should().Be(expected);
        new ConnectWithOptionsViewModel("x", []).ToOptions().Should().Be(ConnectOptions.Default, "nothing chosen means no overrides");
    }

    // ── Default connection ────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task DefaultConnection_IsEditedThroughTheDialog_AndUsedByNewConnections()
    {
        _ = TestHost.MainWindow;
        var settings = AppServices.GetRequired<AppSettingsService>();
        Tree.CreateNewTree();
        try
        {
            using (Tree.EditNode.RegisterHandler(context =>
                   {
                       var dialog = context.Input;
                       dialog.IsDefaultConnection.Should().BeTrue();
                       dialog.Field(nameof(ConnectionInfo.Name)).IsVisible.Should().BeFalse();
                       dialog.Protocol.Value = CoreProtocol.RDP;
                       dialog.Field<ChoiceFieldViewModel>(nameof(ConnectionInfo.Colors)).Value = RDPColors.Colors32Bit;
                       dialog.Field(nameof(ConnectionInfo.Username)).Inherit = true;
                       context.SetOutput(true);
                   }))
            {
                await Tree.EditDefaultConnectionCommand.Execute();
            }

            settings.Current.DefaultConnectionValues.Should().Contain("Colors32Bit");
            settings.Current.DefaultConnectionInheritance.Should().Be("Username");

            var folder = new ContainerInfo { Name = "F" };
            Tree.Root!.AddChild(folder);
            Tree.SelectedNode = Tree.FindNode(folder);
            using (Tree.EditNode.RegisterHandler(context =>
                   {
                       context.Input.Name = "from defaults";
                       context.Input.Hostname = "host.local";
                       context.SetOutput(true);
                   }))
            {
                await Tree.NewConnectionCommand.Execute();
            }

            var created = folder.Children.Should().ContainSingle().Subject;
            (created.Name, created.Protocol, created.Port).Should().Be(("from defaults", CoreProtocol.RDP, 3389));
            created.Colors.Should().Be(RDPColors.Colors32Bit);
            created.Inheritance.Username.Should().BeTrue();

            created.Inheritance.Username = false;
            Tree.SelectedNode = Tree.FindNode(created);
            Tree.ApplyDefaultInheritanceCommand.Execute().Subscribe();
            created.Inheritance.Username.Should().BeTrue();
        }
        finally
        {
            settings.Update(s =>
            {
                s.DefaultConnectionValues = string.Empty;
                s.DefaultConnectionInheritance = string.Empty;
            });
            Tree.ReloadDefaultConnection();
        }
    }

    // ── PuTTY sessions ────────────────────────────────────────────────────

    [AvaloniaFact]
    public void PuttySessions_AppearAsAReadOnlySecondRoot_AndCanBeCopiedIntoTheTree()
    {
        var window = TestHost.MainWindow;
        Tree.CreateNewTree();
        try
        {
            Tree.SetPuttySessions([
                new PuttySession("Jump Host", "jump.example.org", 2222, "ops", "ssh"),
                new PuttySession("Default Settings", "", 22, "", "ssh"),
            ]);
            Dispatcher.UIThread.RunJobs();

            Tree.Nodes.Should().HaveCount(2);
            var puttyRoot = Tree.Nodes[1];
            puttyRoot.IsPuttyRoot.Should().BeTrue();
            puttyRoot.Name.Should().Be("PuTTY Sessions");
            var session = puttyRoot.Children.Should().ContainSingle("PuTTY's \"Default Settings\" is not a session").Subject;
            session.IsReadOnly.Should().BeTrue();
            window.GetVisualDescendants().OfType<TreeViewItem>().Select(i => i.DataContext)
                .Should().Contain(puttyRoot, "the root is rendered");

            Tree.SelectedNode = session;
            ((System.Windows.Input.ICommand)Tree.DeleteSelectedCommand).CanExecute(null).Should().BeFalse();
            ((System.Windows.Input.ICommand)Tree.EditSelectedCommand).CanExecute(null).Should().BeFalse();
            Tree.CanDrop(session, Tree.Nodes[0], TreeDropPosition.Into).Should().BeFalse();

            Tree.DuplicateSelectedCommand.Execute().Subscribe();
            var copy = Tree.Root!.Children.Should().ContainSingle().Subject;
            copy.Should().NotBeOfType<mRemoteNG.Core.Config.Putty.PuttySessionNodeInfo>();
            (copy.Name, copy.Hostname, copy.Port, copy.PuttySession).Should().Be(("Jump Host", "jump.example.org", 2222, "Jump Host"));
            Tree.CreateDialogOptions().PuttySessions.Should().Equal("Jump Host");

            Tree.SearchFilter = "jump.example";
            Dispatcher.UIThread.RunJobs();
            session.IsVisible.Should().BeTrue("search covers PuTTY sessions too");
            Tree.SearchFilter = string.Empty;
        }
        finally
        {
            Tree.SetPuttySessions([]);
        }

        Tree.Nodes.Should().ContainSingle();
    }

    // ── File properties ───────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task FileProperties_ChangeTheMasterPassword_ReloadNeedsTheNewOne()
    {
        _ = TestHost.MainWindow;
        var path = CopyFixture("confCons_v2_6_passwordis_Password.xml");
        Tree.LoadFromFile(path, "Password");
        Tree.IsDirty.Should().BeFalse();

        FilePropertiesDialog? rendered = null;
        using (Tree.EditFileProperties.RegisterHandler(context =>
               {
                   var vm = context.Input;
                   vm.IsPasswordProtected.Should().BeTrue();
                   rendered = new FilePropertiesDialog(vm);
                   rendered.Show();
                   vm.ChangePassword = true;
                   Dispatcher.UIThread.RunJobs();
                   rendered.FindControl<TextBox>("CurrentPasswordBox")!.IsVisible.Should().BeTrue();
                   rendered.FindControl<TextBox>("NewPasswordBox")!.IsVisible.Should().BeTrue();

                   vm.CurrentPassword = "wrong";
                   vm.NewPassword = vm.ConfirmPassword = "N3w master";
                   vm.Engine = mRemoteNG.Core.Security.BlockCipherEngines.Twofish;
                   vm.KeyDerivationIterations = 2000;
                   vm.Validate().Should().ContainSingle(e => e.Contains("current master password"));
                   Dispatcher.UIThread.RunJobs();
                   rendered.FindControl<TextBlock>("ErrorTextBlock")!.IsVisible.Should().BeTrue();

                   vm.CurrentPassword = "Password";
                   context.SetOutput(vm.Validate().Count == 0);
                   rendered.Close();
               }))
        {
            await Tree.FilePropertiesCommand.Execute();
        }

        rendered.Should().NotBeNull();
        Tree.IsDirty.Should().BeTrue("security changes must be saved");
        Tree.SaveToFile();

        var withOld = () => Tree.LoadFromFile(path, "Password");
        withOld.Should().Throw<ConnectionFilePasswordException>();
        Tree.LoadFromFile(path, "N3w master");
        AllNodes().Should().Contain(n => n.Name == "rootConnection");
        TestHost.ViewModel.ConnectionTree.Root!.PasswordString.Should().Be("N3w master");
        AppServices.GetRequired<mRemoteNG.Core.Config.Connections.ConnectionsService>().Encryption.Engine
            .Should().Be(mRemoteNG.Core.Security.BlockCipherEngines.Twofish);
    }

    [AvaloniaFact]
    public async Task EditOnTheRoot_OpensFileProperties()
    {
        _ = TestHost.MainWindow;
        Tree.CreateNewTree();
        Tree.SelectedNode = Tree.Nodes[0];
        var opened = false;
        using var handler = Tree.EditFileProperties.RegisterHandler(context =>
        {
            opened = true;
            context.Input.RootName = "My connections";
            context.SetOutput(true);
        });

        await Tree.EditSelectedCommand.Execute();

        opened.Should().BeTrue();
        Tree.Root!.Name.Should().Be("My connections");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static ListBoxItem PageItem(Window dialog, string title) =>
        dialog.GetVisualDescendants().OfType<ListBoxItem>()
            .Single(i => i.DataContext is PropertyPageViewModel page && page.Title == title);

    /// <summary>The labels of the property rows shown on a page of the connection editor.</summary>
    private static List<string> VisibleLabels(Window dialog, ConnectionDialogViewModel vm, string page)
    {
        vm.SelectedPage = vm.Page(page);
        Dispatcher.UIThread.RunJobs();
        return dialog.GetVisualDescendants().OfType<mRemoteNG.Avalonia.Controls.SettingRow>()
            .Where(r => r.Classes.Contains("field-row") && r.IsEffectivelyVisible)
            .Select(r => r.Header ?? "")
            .ToList();
    }

    private static mRemoteNG.Avalonia.Controls.SettingRow Row(Window dialog, string label) =>
        dialog.GetVisualDescendants().OfType<mRemoteNG.Avalonia.Controls.SettingRow>()
            .First(r => r.Classes.Contains("field-row") && r.IsEffectivelyVisible && r.Header == label);
}
