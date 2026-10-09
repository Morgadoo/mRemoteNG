using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using mRemoteNG.Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views.Sessions;
using mRemoteNG.Protocols.Rdp;
using Xunit;
using static mRemoteNG.Avalonia.Tests.SessionTestSupport;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>
/// Keyboard and focus hand-over around sessions: leaving full screen from a terminal, dialogs opening with the
/// keyboard, the embedded native window (RDP / IntApp) and its tab header, and the connection tree's session dots,
/// empty states and search highlighting.
/// </summary>
public class SessionFocusTests
{
    [AvaloniaFact]
    public async Task CtrlAltEnter_LeavesFullScreen_EvenWhenTheSessionViewHandlesEveryKey()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var tab = (await SessionDock.OpenConnectionAsync(Connection("fs-keys", 1), factory))!;
        Pump();

        // Like the terminal: every key that reaches the view is consumed.
        var swallowed = 0;
        tab.ContentView!.KeyDown += (_, e) =>
        {
            swallowed++;
            e.Handled = true;
        };

        var window = SessionFullScreenWindow.Enter(tab, TestHost.MainWindow);
        Pump();
        tab.ContentView.Focus();
        Pump();

        window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
        swallowed.Should().Be(1, "other keys still go to the session");
        SessionFullScreenWindow.IsFullScreen(tab).Should().BeTrue();

        window.KeyPress(Key.Enter, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.Enter, null);
        Pump();
        SessionFullScreenWindow.IsFullScreen(tab).Should().BeFalse("Ctrl+Alt+Enter is taken before the session view sees it");
        swallowed.Should().Be(1);
        tab.IsDetached.Should().BeFalse();

        await ResetAsync();
    }

    [AvaloniaFact]
    public void ChoosePanelDialog_OpensWithTheKeyboardInTheNameBox()
    {
        var dialog = new ChoosePanelDialog(["General", "Ops", "Night shift"], "Ops");
        dialog.Show(TestHost.MainWindow);
        try
        {
            Pump();
            dialog.InputBox.IsFocused.Should().BeTrue("the first keystroke must not be lost");

            // Up/Down pick an existing panel without leaving the box.
            dialog.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            dialog.Result.Should().Be("Night shift");
            dialog.InputBox.IsFocused.Should().BeTrue();

            // Typing names a new panel.
            dialog.KeyTextInput("Lab");
            dialog.Result.Should().Be("Lab");
        }
        finally
        {
            dialog.Close();
        }
    }

    [AvaloniaFact]
    public void EmbeddedSessionView_OnlyALeftClickOnItsTabHandsTheKeyboardToTheNativeWindow()
    {
        var view = new RdpSessionView(embeddingCandidate: false);
        // A stand-in for the tab header: an element bound to an object whose ContentView is the view.
        var header = new Border
        {
            Height = 30,
            Background = Brushes.Gray,
            DataContext = new FakeTab(view),
        };
        var window = new Window
        {
            Width = 400,
            Height = 300,
            Content = new DockPanel { Children = { header, view } },
        };
        DockPanel.SetDock(header, global::Avalonia.Controls.Dock.Top);

        var tabPresses = 0;
        var otherPresses = 0;
        var released = 0;
        var resumed = 0;
        view.TabHeaderPressed += (_, _) => tabPresses++;
        view.AvaloniaPointerPressed += (_, _) => otherPresses++;
        view.KeyboardReleaseRequested += (_, _) => released++;
        view.KeyboardReleaseEnded += (_, _) => resumed++;

        window.Show();
        try
        {
            Pump();

            // Right click on the tab (context menu): the keyboard stays with Avalonia, so the menu is not closed
            // by the native window taking the focus.
            window.MouseDown(new Point(50, 15), MouseButton.Right);
            window.MouseUp(new Point(50, 15), MouseButton.Right);
            Pump();
            tabPresses.Should().Be(0);
            otherPresses.Should().Be(1);

            // Middle click (closes the tab): same.
            window.MouseDown(new Point(50, 15), MouseButton.Middle);
            window.MouseUp(new Point(50, 15), MouseButton.Middle);
            Pump();
            tabPresses.Should().Be(0);

            // Left click: go to the remote desktop.
            window.MouseDown(new Point(50, 15), MouseButton.Left);
            window.MouseUp(new Point(50, 15), MouseButton.Left);
            Pump();
            tabPresses.Should().Be(1);

            // A dialog opening over the session takes the keyboard back from the native window until it closes.
            var dialog = new Window { Width = 200, Height = 100 };
            dialog.Show(window);
            Pump();
            released.Should().Be(1);
            view.IsCoveredByWindow.Should().BeTrue();
            dialog.Close();
            Pump();
            resumed.Should().Be(1);
            view.IsCoveredByWindow.Should().BeFalse();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TreeRows_ShowADotForOpenSessions()
    {
        await ResetAsync();
        var tree = TestHost.ViewModel.ConnectionTree;
        tree.CreateNewTree();
        tree.IsTreeEmpty.Should().BeTrue("a new file has no connections");

        var node = tree.AddConnection("dot", Core.Connection.Protocol.ProtocolType.RAW, "127.0.0.1", 1)!;
        tree.IsTreeEmpty.Should().BeFalse();
        node.HasOpenSession.Should().BeFalse();

        var factory = new FakeProtocolFactory();
        var tab = (await SessionDock.OpenConnectionAsync(node.Model, factory))!;
        Pump();
        node.SessionIndicator.Should().Be(SessionIndicator.Connected);
        node.IsSessionConnected.Should().BeTrue();

        factory.Created.Last().Drop();
        Pump();
        node.SessionIndicator.Should().Be(SessionIndicator.Failed);

        await SessionDock.CloseSessionAsync(tab);
        Pump();
        node.HasOpenSession.Should().BeFalse("the dot goes away with the last session");

        await ResetAsync();
        tree.CreateNewTree();
    }

    [AvaloniaFact]
    public void TreeSearch_ReportsNoResults_AndHighlightsMatches()
    {
        var tree = TestHost.ViewModel.ConnectionTree;
        tree.CreateNewTree();
        var node = tree.AddConnection("Web server", Core.Connection.Protocol.ProtocolType.SSH2, "web01", 22)!;

        tree.SearchFilter = "server";
        tree.HasNoSearchResults.Should().BeFalse();
        node.SearchHighlight.Should().Be("server");
        HighlightTextBlock.FindMatches(node.Name, node.SearchHighlight).Should().Equal((4, 6));

        // The search box's hook: Down moves into the results.
        var view = TestHost.MainWindow.GetVisualDescendants().OfType<Views.ConnectionTreeView>().Single();
        Pump();
        view.HandleSearchKey(new KeyEventArgs { Key = Key.Down }).Should().BeTrue();
        Pump();
        view.IsKeyboardFocusWithin.Should().BeTrue();
        view.HandleSearchKey(new KeyEventArgs { Key = Key.A }).Should().BeFalse();
        Views.ConnectionTreeView.CreateMoreActionsMenu(tree).Items.Should().HaveCount(5);

        tree.SearchFilter = "no such thing";
        tree.HasNoSearchResults.Should().BeTrue();

        tree.SearchFilter = string.Empty;
        tree.HasNoSearchResults.Should().BeFalse();
        HighlightTextBlock.FindMatches("aAa", "a").Should().HaveCount(3, "matching ignores case");

        // The row renders the matches as separate runs and goes back to plain text when the search is cleared.
        var text = new HighlightTextBlock { Text = "Web server", Highlight = "SERVER" };
        text.Inlines!.Count.Should().Be(2);
        text.Highlight = null;
        text.Inlines.Count.Should().Be(0);
        text.Text.Should().Be("Web server");

        tree.CreateNewTree();
    }

    private sealed class FakeTab(Control view)
    {
        public Control ContentView { get; } = view;
    }
}
