using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using FluentAssertions;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views.Sessions;
using Xunit;
using static mRemoteNG.Avalonia.Tests.SessionTestSupport;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>Dragging session tabs: reorder within a panel, move to another panel or split into a new region.</summary>
public class SessionTabDragTests
{
    private static SessionAreaView Area => TestHost.MainWindow.FindControl<SessionAreaView>("SessionArea")!;

    [AvaloniaFact]
    public async Task DropOnAnotherPanelsTabs_MovesTheSessionToThePointer()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var a1 = (await SessionDock.OpenConnectionAsync(Connection("a1", 1), factory))!;
        var a2 = (await SessionDock.OpenConnectionAsync(Connection("a2", 1), factory))!;
        var b1 = (await SessionDock.OpenConnectionAsync(Connection("b1", 1, panel: "Right"), factory))!;
        SessionDock.Arrangement = PanelArrangement.SideBySide;
        Pump();
        var general = a1.Panel!;
        var right = b1.Panel!;
        var rightView = Area.PanelViews[right];

        // Over the left half of b1: the indicator shows where the tab goes.
        var target = TabPoint(rightView, 0, 0.25);
        Drag(target, RawDragEventType.DragEnter, a2);
        Drag(target, RawDragEventType.DragOver, a2);
        rightView.DropIndicatorX.Should().NotBeNull();
        Drag(target, RawDragEventType.Drop, a2);
        Pump();

        rightView.DropIndicatorX.Should().BeNull();
        right.Sessions.Should().Equal(a2, b1);
        general.Sessions.Should().Equal(a1);
        a2.Panel.Should().BeSameAs(right);
        right.ActiveSession.Should().BeSameAs(a2);
        SessionDock.ActivePanel.Should().BeSameAs(right);
        rightView.Hosts[a2].HostedView.Should().BeSameAs(a2.ContentView, "the session view moves with the tab");
        Area.PanelViews[general].Hosts.Should().NotContainKey(a2);

        // Dropping on the panel body (below the tabs) appends.
        var body = rightView.TranslatePoint(new Point(rightView.Bounds.Width / 2, rightView.Bounds.Height - 20), TestHost.MainWindow)!.Value;
        Drag(body, RawDragEventType.DragEnter, a1);
        Drag(body, RawDragEventType.Drop, a1);
        Pump();
        right.Sessions.Should().Equal(a2, b1, a1);
        general.Sessions.Should().BeEmpty();

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task DropWithinThePanel_ReordersTheTabs()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var tabs = new List<SessionTabViewModel>();
        foreach (var name in new[] { "t1", "t2", "t3" })
            tabs.Add((await SessionDock.OpenConnectionAsync(Connection(name, 1), factory))!);
        var panel = tabs[0].Panel!;
        var view = Area.PanelViews[panel];

        // t1 onto the right half of t3: last.
        var end = TabPoint(view, 2, 0.75);
        Drag(end, RawDragEventType.DragEnter, tabs[0]);
        Drag(end, RawDragEventType.Drop, tabs[0]);
        Pump();
        panel.Sessions.Should().Equal(tabs[1], tabs[2], tabs[0]);
        panel.ActiveSession.Should().BeSameAs(tabs[0]);

        // t1 (now last) onto the left half of the first tab: first again.
        var start = TabPoint(view, 0, 0.25);
        Drag(start, RawDragEventType.DragEnter, tabs[0]);
        Drag(start, RawDragEventType.Drop, tabs[0]);
        Pump();
        panel.Sessions.Should().Equal(tabs);
        view.Hosts.Keys.Should().BeEquivalentTo(tabs, "reordering keeps every session view");

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task DropOnThePanelStrip_MovesIntoThatPanel_OrANewOne()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var a = (await SessionDock.OpenConnectionAsync(Connection("a", 1), factory))!;
        var b = (await SessionDock.OpenConnectionAsync(Connection("b", 1), factory))!;
        var ops = (await SessionDock.OpenConnectionAsync(Connection("o", 1, panel: "Ops"), factory))!;
        var general = a.Panel!;
        SessionDock.ActivePanel = general;
        Pump();
        Area.IsPanelStripVisible.Should().BeTrue();
        var strip = Area.FindControl<StackPanel>("PanelStrip")!;

        var opsButton = strip.Children.OfType<ToggleButton>().Single(t => ReferenceEquals(t.Tag, ops.Panel));
        var center = Center(opsButton);
        Drag(center, RawDragEventType.DragEnter, b);
        Drag(center, RawDragEventType.DragOver, b);
        opsButton.Classes.Should().Contain("drop-target");
        Drag(center, RawDragEventType.Drop, b);
        Pump();
        ops.Panel!.Sessions.Should().Equal(ops, b);
        general.Sessions.Should().Equal(a);

        // The "+" button makes a new panel for the tab.
        strip = Area.FindControl<StackPanel>("PanelStrip")!;
        var add = strip.Children.OfType<Button>().Last();
        Drag(Center(add), RawDragEventType.DragEnter, a);
        Drag(Center(add), RawDragEventType.Drop, a);
        Pump();
        a.Panel!.Name.Should().StartWith("New Panel");
        a.Panel.Sessions.Should().Equal(a);
        SessionDock.Panels.Should().HaveCount(3);

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task DropOnTheRightEdge_SplitsTheAreaInHalf()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var keep = (await SessionDock.OpenConnectionAsync(Connection("keep", 1), factory))!;
        var moved = (await SessionDock.OpenConnectionAsync(Connection("moved", 1), factory))!;
        var general = keep.Panel!;
        Pump();
        TestHost.MainWindow.UpdateLayout();

        // Dragging onto the right half of the panel body previews the right half without showing the tab reorder indicator.
        var generalView = Area.PanelViews[general];
        var rightEdge = generalView.TranslatePoint(
            new Point(generalView.Bounds.Width - 30, generalView.Bounds.Height / 2),
            TestHost.MainWindow)!.Value;
        Drag(rightEdge, RawDragEventType.DragEnter, moved);
        Drag(rightEdge, RawDragEventType.DragOver, moved);
        Area.IsSplitPreviewVisible.Should().BeTrue();
        generalView.DropIndicatorX.Should().BeNull("splitting the body does not fight the tab strip reorder indicator");
        Drag(rightEdge, RawDragEventType.Drop, moved);
        Pump();
        TestHost.MainWindow.UpdateLayout();

        Area.IsSplitPreviewVisible.Should().BeFalse();
        SessionDock.Arrangement.Should().Be(PanelArrangement.SplitVertical);
        moved.Panel.Should().NotBeSameAs(general);
        moved.Panel!.IsSplitPanel.Should().BeTrue("the generated split panel is an internal container");
        SessionDock.PanelNames.Should().NotContain(moved.Panel.Name, "split panels are hidden from user-facing panel lists");
        moved.Panel.Section.Should().Be(PanelSection.Secondary);
        general.Sessions.Should().Equal(keep);
        var grid = Area.FindControl<Grid>("PanelGrid")!;
        grid.ColumnDefinitions.Should().HaveCount(3, "the left half, a splitter and the right half");
        grid.Children.OfType<GridSplitter>().Should().ContainSingle();
        Area.PanelViews[moved.Panel].IsVisible.Should().BeTrue();
        Area.PanelViews[general].IsVisible.Should().BeTrue();
        Area.PanelViews[moved.Panel].ShowHeader.Should().BeFalse();
        Area.PanelViews[general].ShowHeader.Should().BeFalse();
        Area.PanelViews[moved.Panel].Hosts[moved].HostedView.Should().BeSameAs(moved.ContentView);

        // Dropping the remaining single tab onto the bottom edge of the other panel re-splits top/bottom.
        var movedView = Area.PanelViews[moved.Panel];
        var bottomEdge = movedView.TranslatePoint(
            new Point(movedView.Bounds.Width / 2, movedView.Bounds.Height - 30),
            TestHost.MainWindow)!.Value;
        Drag(bottomEdge, RawDragEventType.DragEnter, keep);
        Drag(bottomEdge, RawDragEventType.Drop, keep);
        Pump();
        TestHost.MainWindow.UpdateLayout();

        SessionDock.Arrangement.Should().Be(PanelArrangement.SplitHorizontal);
        moved.Panel!.Section.Should().Be(PanelSection.Primary, "the other half stays opposite the dropped tab");
        keep.Panel.Should().BeSameAs(general, "a panel holding only this tab moves as a whole");
        keep.Panel!.Section.Should().Be(PanelSection.Secondary);
        grid.RowDefinitions.Should().HaveCount(3);

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task NestedSplits_AndClosingSplitSession_CollapsesEmptyRegion()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var s1 = (await SessionDock.OpenConnectionAsync(Connection("s1", 1), factory))!;
        var s2 = (await SessionDock.OpenConnectionAsync(Connection("s2", 1), factory))!;
        var s3 = (await SessionDock.OpenConnectionAsync(Connection("s3", 1), factory))!;
        Pump();
        TestHost.MainWindow.UpdateLayout();

        // Split s2 to the right.
        var generalView = Area.PanelViews[s1.Panel!];
        var rightEdge = generalView.TranslatePoint(
            new Point(generalView.Bounds.Width - 30, generalView.Bounds.Height / 2),
            TestHost.MainWindow)!.Value;
        Drag(rightEdge, RawDragEventType.DragEnter, s2);
        Drag(rightEdge, RawDragEventType.Drop, s2);
        Pump();
        TestHost.MainWindow.UpdateLayout();

        // Now split s3 into the bottom half of the right panel (nested split: left | (top / bottom)).
        var rightView = Area.PanelViews[s2.Panel!];
        var rightBottomEdge = rightView.TranslatePoint(
            new Point(rightView.Bounds.Width / 2, rightView.Bounds.Height - 30),
            TestHost.MainWindow)!.Value;
        Drag(rightBottomEdge, RawDragEventType.DragEnter, s3);
        Drag(rightBottomEdge, RawDragEventType.Drop, s3);
        Pump();
        TestHost.MainWindow.UpdateLayout();

        SessionDock.LayoutTree.Should().NotBeNull();
        SessionDock.LayoutTree!.Vertical.Should().BeTrue();
        SessionDock.LayoutTree.Second!.IsLeaf.Should().BeFalse("the right region is nested into top and bottom");
        SessionDock.LayoutTree.Second.Vertical.Should().BeFalse();
        Area.PanelViews[s1.Panel!].IsVisible.Should().BeTrue();
        Area.PanelViews[s2.Panel!].IsVisible.Should().BeTrue();
        Area.PanelViews[s3.Panel!].IsVisible.Should().BeTrue();

        // Closing s3 removes its anonymous split panel and collapses the nested split back to two columns.
        var s3Panel = s3.Panel!;
        await SessionDock.CloseSessionAsync(s3);
        Pump();
        SessionDock.Panels.Should().NotContain(s3Panel);
        SessionDock.LayoutTree!.Second!.IsLeaf.Should().BeTrue();

        // Closing s2 removes the remaining split panel and returns the layout to a single unsplit panel.
        var s2Panel = s2.Panel!;
        await SessionDock.CloseSessionAsync(s2);
        Pump();
        SessionDock.Panels.Should().NotContain(s2Panel);
        SessionDock.LayoutTree.Should().BeNull();
        SessionDock.Arrangement.Should().Be(PanelArrangement.Tabbed);

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task SplitMenu_MovesTheTabIntoTheChosenHalf()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var first = (await SessionDock.OpenConnectionAsync(Connection("first", 1), factory))!;
        var second = (await SessionDock.OpenConnectionAsync(Connection("second", 1), factory))!;

        var menu = SessionTabMenu.Build(second, TestHost.MainWindow);
        SessionTabMenu.Invoke(SessionTabMenu.Find(menu, SessionTabMenu.SplitRight)!);
        Pump();

        SessionDock.Arrangement.Should().Be(PanelArrangement.SplitVertical);
        second.Panel!.Section.Should().Be(PanelSection.Secondary);
        second.Panel.IsSplitPanel.Should().BeTrue();
        first.Panel!.Sessions.Should().Equal(first);
        Area.FindControl<Grid>("PanelGrid")!.ColumnDefinitions.Should().HaveCount(3);

        // Move to Panel menu does not list anonymous split panels.
        var firstMenu = SessionTabMenu.Build(first, TestHost.MainWindow);
        var moveItem = SessionTabMenu.Find(firstMenu, SessionTabMenu.MoveToPanel)!;
        moveItem.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).Should().NotContain(second.Panel.Name);

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task OtherDragData_IsIgnored()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var a = (await SessionDock.OpenConnectionAsync(Connection("a", 1), factory))!;
        var b = (await SessionDock.OpenConnectionAsync(Connection("b", 1), factory))!;
        var panel = a.Panel!;
        var view = Area.PanelViews[panel];

        var text = new DataObject();
        text.Set(DataFormats.Text, "a");
        var point = TabPoint(view, 0, 0.25);
        TestHost.MainWindow.DragDrop(point, RawDragEventType.DragEnter, text, DragDropEffects.Move);
        TestHost.MainWindow.DragDrop(point, RawDragEventType.DragOver, text, DragDropEffects.Move);
        view.DropIndicatorX.Should().BeNull();
        TestHost.MainWindow.DragDrop(point, RawDragEventType.Drop, text, DragDropEffects.Move);
        Pump();

        panel.Sessions.Should().Equal(a, b);

        await ResetAsync();
    }

    /// <summary>A point in the window over tab <paramref name="index"/> at <paramref name="fraction"/> of its width.</summary>
    private static Point TabPoint(SessionPanelView view, int index, double fraction)
    {
        Pump();
        TestHost.MainWindow.UpdateLayout();
        var strip = view.FindControl<ListBox>("TabStrip")!;
        var container = (Control)strip.ContainerFromIndex(index)!;
        return container.TranslatePoint(new Point(container.Bounds.Width * fraction, container.Bounds.Height / 2), TestHost.MainWindow)!.Value;
    }

    private static Point Center(Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), TestHost.MainWindow)!.Value;

    private static void Drag(Point point, RawDragEventType type, SessionTabViewModel tab)
    {
        var data = new DataObject();
        data.Set(SessionPanelView.TabDragFormat, tab);
        TestHost.MainWindow.DragDrop(point, type, data, DragDropEffects.Move);
        Pump();
    }
}
