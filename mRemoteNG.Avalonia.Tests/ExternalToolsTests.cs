using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Tools;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.External;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>External Tools window, toolbar, menu and IntApp sessions, with the real DI container (headless).</summary>
public class ExternalToolsTests
{
    private static ExternalToolPlatform OtherOs =>
        ExternalTool.CurrentPlatform == ExternalToolPlatform.Windows ? ExternalToolPlatform.Linux : ExternalToolPlatform.Windows;

    private static ExternalToolsService Service()
    {
        _ = TestHost.MainWindow;
        var service = AppServices.GetRequired<ExternalToolsService>();
        service.ReplaceAll(
        [
            new ExternalTool("Ping", "ping", "%HOSTNAME%"),
            new ExternalTool("Hidden", "true") { ShowOnToolbar = false },
            new ExternalTool("Other OS", "cmd") { Platform = OtherOs },
        ]);
        return service;
    }

    [AvaloniaFact]
    public void Service_IsWiredToTheLogPanelAndSessions()
    {
        var service = Service();

        service.OpenIntegratedSession.Should().NotBeNull();
        service.VariablesFilter.Should().NotBeNull();
        service.Repository.FilePath.Should().StartWith(TestHost.ConfigDirectory);

        var log = AppServices.GetRequired<LogPanelDockable>();
        _ = service.RunAsync("no such tool", null);
        Dispatcher.UIThread.RunJobs();
        log.Entries.Should().Contain(e => e.Message.Contains("no such tool"));
    }

    [AvaloniaFact]
    public void Toolbar_ShowsToolsMarkedForTheToolbar_AndFollowsChanges()
    {
        var service = Service();
        var toolbar = AppServices.GetRequired<ExternalToolsToolbarViewModel>();

        toolbar.Buttons.Select(b => b.DisplayName).Should().Equal("Ping");

        service.Tools[1].ShowOnToolbar = true;
        toolbar.Buttons.Select(b => b.DisplayName).Should().Equal("Ping", "Hidden");

        var control = new ExternalToolsToolbar { DataContext = toolbar };
        var window = new Window { Content = control };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        control.GetVisualDescendants().OfType<Button>().Count(b => b.DataContext is ExternalToolCommandItem).Should().Be(2);
        control.FindControl<Button>("OverflowButton")!.IsVisible.Should().BeFalse("two tools fit on the toolbar");

        // Beyond the inline limit the rest go to the "⋯" menu.
        for (var i = 0; i < ExternalToolsToolbarViewModel.MaxInlineButtons + 1; i++)
            service.Tools.Add(new ExternalTool($"Extra {i}", "x") { ShowOnToolbar = true });
        Dispatcher.UIThread.RunJobs();
        toolbar.InlineButtons.Should().HaveCount(ExternalToolsToolbarViewModel.MaxInlineButtons);
        toolbar.OverflowButtons.Should().HaveCount(toolbar.Buttons.Count - ExternalToolsToolbarViewModel.MaxInlineButtons);
        control.GetVisualDescendants().OfType<Button>().Count(b => b.DataContext is ExternalToolCommandItem)
            .Should().Be(ExternalToolsToolbarViewModel.MaxInlineButtons);
        control.FindControl<Button>("OverflowButton")!.IsVisible.Should().BeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public void Menu_ListsEveryTool_ToolsForAnotherOsDisabled()
    {
        var service = Service();

        var items = ExternalToolsMenu.BuildItems(service, () => null);

        items.Select(i => i.Header).Should().Equal("Ping", "Hidden", $"Other OS ({OtherOs})");
        items.Select(i => i.IsEnabled).Should().Equal(true, true, false);

        var parent = new MenuItem { Header = "External Tools" };
        ExternalToolsMenu.Attach(parent, service, () => null);
        service.ReplaceAll([new ExternalTool("Only", "x")]);
        parent.Items.OfType<MenuItem>().Select(i => i.Header).Should().Equal("Only");
    }

    [AvaloniaFact]
    public void Window_EditsACopy_SaveWritesIt_CancelDiscards()
    {
        var service = Service();
        var target = new ConnectionInfo { Name = "web", Hostname = "web01", Password = "secret" };

        var vm = new ExternalToolsWindowViewModel(service, target);
        vm.Preview.Should().Be("ping web01");
        vm.AddCommand.Execute().Subscribe();
        vm.Selected!.FileName = "xterm";
        vm.Selected.Arguments = "-T \"%PASSWORD%\"";
        vm.Preview.Should().NotContain("secret", "passwords are masked in the preview");
        vm.IsDirty.Should().BeTrue();
        service.Tools.Should().HaveCount(3, "nothing is saved yet");

        vm.Save().Should().BeTrue();

        service.Tools.Select(t => t.DisplayName).Should().Equal("Ping", "Hidden", "Other OS", "New External Tool");
        new ExternalToolsRepository(service.Repository.FilePath).Load()!.Should().HaveCount(4);

        var cancelled = new ExternalToolsWindowViewModel(service, null);
        cancelled.Tools[0].DisplayName = "Renamed";
        cancelled.CancelCommand.Execute().Subscribe();
        service.Tools[0].DisplayName.Should().Be("Ping");
    }

    [AvaloniaFact]
    public void Window_RejectsDuplicateAndEmptyNames()
    {
        var service = Service();
        var vm = new ExternalToolsWindowViewModel(service);

        vm.Tools[1].DisplayName = "ping";
        vm.Save().Should().BeFalse();
        vm.StatusMessage.Should().Contain("More than one tool");

        vm.Tools[1].DisplayName = " ";
        vm.Save().Should().BeFalse();
        vm.StatusIsError.Should().BeTrue();
    }

    [AvaloniaFact]
    public void Window_Renders()
    {
        Service();
        var window = new ExternalToolsWindow(new ExternalToolsWindowViewModel(AppServices.GetRequired<ExternalToolsService>()));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.GetVisualDescendants().OfType<ListBoxItem>().Should().HaveCount(3);
        window.FindControl<TextBox>("DisplayNameBox")!.Text.Should().Be("Ping");
        window.Close();
    }

    [AvaloniaFact]
    public async Task IntAppSession_WithAMissingTool_ShowsTheError()
    {
        Service();
        var sessions = AppServices.GetRequired<SessionsDockable>();
        var connection = new ConnectionInfo { Name = "x", Protocol = CoreProtocol.IntApp, ExtApp = "Not configured" };

        var tab = await sessions.OpenConnectionAsync(connection, AppServices.GetRequired<IProtocolFactory>());
        Dispatcher.UIThread.RunJobs();

        tab.Should().NotBeNull();
        tab!.Protocol.Should().BeOfType<IntegratedProgramProtocol>();
        tab.Protocol.State.Should().Be(ConnectionState.Error);
        tab.StatusText.Should().Contain("Not configured");
        await sessions.CloseSessionAsync(tab);
    }

    [Fact]
    public void DefaultUsername_FillsAnEmptyUsernameOnly()
    {
        ExternalToolsIntegration.ApplyDefaultUsername(new ExternalToolVariables(), "admin").Username.Should().Be("admin");
        ExternalToolsIntegration.ApplyDefaultUsername(new ExternalToolVariables { Username = "bob" }, "admin").Username.Should().Be("bob");
        ExternalToolsIntegration.ApplyDefaultUsername(new ExternalToolVariables(), "").Username.Should().BeEmpty();
    }
}
