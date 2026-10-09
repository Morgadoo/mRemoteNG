using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views.Sessions;
using mRemoteNG.Protocols.Abstractions;
using Xunit;
using static mRemoteNG.Avalonia.Tests.SessionTestSupport;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>Named panels: opening into a panel, arrangements, floating windows and the persisted layout.</summary>
public class SessionPanelTests
{
    private static SessionAreaView Area => TestHost.MainWindow.FindControl<SessionAreaView>("SessionArea")!;

    [AvaloniaFact]
    public async Task Connections_OpenInTheirNamedPanel_OrTheOneChosenInConnectOptions()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();

        var general = (await SessionDock.OpenConnectionAsync(Connection("g", 1), factory))!;
        var ops = (await SessionDock.OpenConnectionAsync(Connection("o", 1, panel: "Ops"), factory))!;
        var chosen = (await SessionDock.OpenConnectionAsync(Connection("x", 1, panel: "Ops"), factory,
            new ConnectOptions { Panel = "Night shift" }))!;

        general.Panel!.Name.Should().Be("General");
        ops.Panel!.Name.Should().Be("Ops");
        chosen.Panel!.Name.Should().Be("Night shift", "ConnectOptions.Panel overrides the connection's panel");
        SessionDock.Panels.Select(p => p.Name).Should().Equal("General", "Ops", "Night shift");
        SessionDock.ActivePanel.Should().BeSameAs(chosen.Panel);

        // Tabbed: one panel visible, the panel strip shows all three.
        Pump();
        Area.IsPanelStripVisible.Should().BeTrue();
        Area.PanelViews[chosen.Panel].IsVisible.Should().BeTrue();
        Area.PanelViews[general.Panel].IsVisible.Should().BeFalse();

        // "Always show panel selection" asks, and the answer wins.
        Settings.Update(s => s.AlwaysShowPanelSelectionDlg = true);
        var originalChooser = SessionDock.PanelChooser;
        SessionDock.PanelChooser = (names, suggestion) =>
        {
            names.Should().Contain("Ops");
            suggestion.Should().Be("Ops");
            return Task.FromResult<string?>("General");
        };
        try
        {
            var asked = (await SessionDock.OpenConnectionAsync(Connection("asked", 1, panel: "Ops"), factory))!;
            asked.Panel!.Name.Should().Be("General");

            SessionDock.PanelChooser = (_, _) => Task.FromResult<string?>(null);
            (await SessionDock.OpenConnectionAsync(Connection("cancelled", 1, panel: "Ops"), factory)).Should().BeNull();
        }
        finally
        {
            SessionDock.PanelChooser = originalChooser;
        }

        // Move to another panel.
        SessionDock.MoveSession(general, "Ops");
        general.Panel.Name.Should().Be("Ops");
        SessionDock.ActiveSession.Should().BeSameAs(general);

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task Panels_SplitSideBySide_Float_AndDockBack()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var a = (await SessionDock.OpenConnectionAsync(Connection("a", 1), factory))!;
        var b = (await SessionDock.OpenConnectionAsync(Connection("b", 1, panel: "Right"), factory))!;
        var grid = Area.FindControl<Grid>("PanelGrid")!;

        SessionDock.Arrangement = PanelArrangement.SideBySide;
        Pump();
        grid.ColumnDefinitions.Should().HaveCount(3, "two panels and a splitter");
        grid.Children.OfType<GridSplitter>().Should().ContainSingle();
        Area.PanelViews[a.Panel!].IsVisible.Should().BeTrue();
        Area.PanelViews[b.Panel!].IsVisible.Should().BeTrue();
        Area.PanelViews[a.Panel!].ShowHeader.Should().BeTrue();
        Area.IsPanelStripVisible.Should().BeFalse();

        SessionDock.Arrangement = PanelArrangement.Stacked;
        Pump();
        grid.RowDefinitions.Should().HaveCount(3);
        grid.ColumnDefinitions.Should().BeEmpty();

        // Float the right panel: its view (and the session control) moves to a window.
        var right = b.Panel!;
        right.IsFloating = true;
        Pump();
        Area.FloatingWindows.Should().ContainKey(right);
        var window = Area.FloatingWindows[right];
        window.IsVisible.Should().BeTrue();
        window.PanelView.Should().BeSameAs(Area.PanelViews[right]);
        Area.DockedPanels.Should().Equal(a.Panel!);
        Area.PanelViews[right].Hosts[b].HostedView.Should().BeSameAs(b.ContentView);

        // Closing the window docks the panel back; the session stays open.
        window.Close();
        await WaitUntil(() => !right.IsFloating);
        Area.FloatingWindows.Should().BeEmpty();
        Area.DockedPanels.Should().Equal(a.Panel!, right);
        grid.Children.Should().Contain(Area.PanelViews[right]);
        SessionDock.Sessions.Should().Contain(b);

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task Layout_IsPersistedInSettings_AndRestored()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var window = TestHost.MainWindow;
        var vm = TestHost.ViewModel;
        await SessionDock.OpenConnectionAsync(Connection("a", 1), factory);
        var b = (await SessionDock.OpenConnectionAsync(Connection("b", 1, panel: "Tools"), factory))!;
        SessionDock.NewPanel("Scratch");
        SessionDock.Arrangement = PanelArrangement.SideBySide;
        SessionDock.FindPanel("Tools")!.SizeWeight = 3;
        SessionDock.FindPanel("Scratch")!.FloatingBounds = new FloatingBounds(10, 20, 500, 400);
        SessionDock.FindPanel("Scratch")!.IsFloating = true;
        vm.IsConnectionTreeVisible = false;
        vm.IsMultiSshToolbarVisible = true;
        Pump();

        window.SaveLayout();
        // Saving stores the splitter positions (pixels) as the weights.
        var toolsShare = SessionDock.FindPanel("Tools")!.SizeWeight / SessionDock.FindPanel("General")!.SizeWeight;
        toolsShare.Should().BeApproximately(3, 0.1);
        var json = Settings.Current.WindowLayout;
        json.Should().Contain("SideBySide").And.Contain("Scratch");
        WindowLayoutState.FromJson(json)!.Panels.Single(p => p.Name == "Scratch").Floating
            .Should().Be(new FloatingBounds(10, 20, 500, 400));

        // Reset Layout restores the defaults…
        vm.ResetLayoutCommand.Execute().Subscribe();
        Pump();
        SessionDock.Arrangement.Should().Be(PanelArrangement.Tabbed);
        SessionDock.Panels.Should().OnlyContain(p => !p.IsFloating);
        vm.IsConnectionTreeVisible.Should().BeTrue();
        vm.IsMultiSshToolbarVisible.Should().BeFalse();

        // …and the saved layout brings the arrangement back (panels missing at startup are created).
        await SessionDock.ClosePanelAsync(SessionDock.FindPanel("Scratch")!);
        window.RestoreLayoutFromSettings(vm);
        Pump();
        SessionDock.Arrangement.Should().Be(PanelArrangement.SideBySide);
        SessionDock.Panels.Select(p => p.Name).Should().Equal("General", "Tools", "Scratch");
        (SessionDock.FindPanel("Tools")!.SizeWeight / SessionDock.FindPanel("General")!.SizeWeight).Should().BeApproximately(toolsShare, 0.001);
        var scratch = SessionDock.FindPanel("Scratch")!;
        scratch.IsFloating.Should().BeTrue();
        Area.FloatingWindows.Should().ContainKey(scratch);
        Area.FloatingWindows[scratch].Width.Should().Be(500, "the floating window reopens with its saved size");
        vm.IsConnectionTreeVisible.Should().BeFalse();
        vm.IsMultiSshToolbarVisible.Should().BeTrue();
        b.Panel!.Name.Should().Be("Tools");

        // Corrupt or empty layouts fall back to the defaults.
        WindowLayoutState.FromJson("{not json").Should().BeNull();
        WindowLayoutState.FromJson("").Should().BeNull();
        WindowLayoutState.FromJson("{\"Width\":-5,\"Arrangement\":\"Stacked\"}")!.Width.Should().Be(WindowLayoutState.DefaultWidth);

        vm.IsConnectionTreeVisible = true;
        vm.IsMultiSshToolbarVisible = false;
        await ResetAsync();
    }
}
