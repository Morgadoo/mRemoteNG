using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Avalonia.Views.Shell;
using mRemoteNG.Core.Connection;
using mRemoteNG.Protocols.Ssh;
using Xunit;
using static mRemoteNG.Avalonia.Tests.SessionTestSupport;
using CoreProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>The main window shell: menu shortcuts, the command palette, toasts, status bar and empty state.</summary>
public class ShellTests
{
    private static MainWindowViewModel Vm => TestHost.ViewModel;

    private static ConnectionTreeViewModel Tree => Vm.ConnectionTree;

    private static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Pump();
    }

    private static void FocusTree(MainWindow window)
    {
        window.GetVisualDescendants().OfType<TreeView>().Single().Focus();
        Pump();
    }

    private static void CloseOwnedWindows(Window window)
    {
        foreach (var owned in window.OwnedWindows.ToList())
            owned.Close();
        Pump();
    }

    // ── Keyboard shortcuts ────────────────────────────────────────────────

    [AvaloniaFact]
    public void EveryMenuGesture_IsAWindowKeyBinding()
    {
        var window = TestHost.MainWindow;
        var menu = window.FindControl<Menu>("MainMenu")!;
        var handledElsewhere = new[] { "Alt+F4", "Ctrl+Tab", "Ctrl+Shift+Tab" }.Select(KeyGesture.Parse).ToList();

        var shown = AppShortcuts.MenuItems(menu)
            .Select(m => m.Item.InputGesture)
            .OfType<KeyGesture>()
            .Where(g => !handledElsewhere.Contains(g))
            .ToList();

        shown.Should().Contain(KeyGesture.Parse("Ctrl+,")).And.Contain(KeyGesture.Parse("Ctrl+Shift+N"))
            .And.Contain(KeyGesture.Parse("Ctrl+O")).And.Contain(KeyGesture.Parse("Ctrl+K"));
        var bound = window.KeyBindings.Select(b => b.Gesture).ToList();
        bound.Should().Contain(shown);
        bound.Should().Contain(KeyGesture.Parse("Ctrl+Shift+P"), "the palette's second gesture");
        bound.Should().OnlyHaveUniqueItems();
    }

    [AvaloniaFact]
    public void CtrlComma_OpensOptions()
    {
        var window = TestHost.MainWindow;
        CloseOwnedWindows(window);
        FocusTree(window);

        var runs = 0;
        // (Headless runs have no desktop lifetime: the command finds no owner window and completes at once.)
        using var subscription = Vm.OpenOptionsCommand.Subscribe(_ => runs++);

        Press(window, PhysicalKey.Comma, RawInputModifiers.Control);

        runs.Should().Be(1, "Ctrl+, is the menu's Options shortcut");
        CloseOwnedWindows(window);
    }

    [AvaloniaFact]
    public void ShortcutPolicy_LeavesTerminalAndTextKeysAlone()
    {
        static bool Allows(string gesture, ShortcutFocus focus) => ShortcutPolicy.Allows(KeyGesture.Parse(gesture), focus);

        foreach (var gesture in new[] { "Ctrl+C", "Ctrl+D", "Ctrl+E", "Ctrl+K", "Ctrl+N", "Ctrl+O", "Ctrl+Q", "Ctrl+S", "Ctrl+J", "Ctrl+F", "Delete", "Ctrl+Shift+C", "Ctrl+Shift+V" })
            Allows(gesture, ShortcutFocus.Terminal).Should().BeFalse($"{gesture} belongs to the shell");
        foreach (var gesture in new[] { "Ctrl+Shift+P", "Ctrl+Shift+T", "Ctrl+Shift+L", "Ctrl+Shift+N", "F11" })
            Allows(gesture, ShortcutFocus.Terminal).Should().BeTrue($"{gesture} is an app chord");

        Allows("Ctrl+Shift+P", ShortcutFocus.RemoteSession).Should().BeFalse("a remote desktop gets every key");
        Allows("F11", ShortcutFocus.RemoteSession).Should().BeTrue();

        foreach (var gesture in new[] { "Delete", "Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+Z", "Shift+Delete", "Ctrl+Left" })
            Allows(gesture, ShortcutFocus.TextInput).Should().BeFalse($"{gesture} edits text");
        foreach (var gesture in new[] { "Ctrl+K", "Ctrl+S", "Ctrl+,", "Ctrl+N", "F11" })
            Allows(gesture, ShortcutFocus.TextInput).Should().BeTrue();

        Allows("Delete", ShortcutFocus.Window).Should().BeTrue();
        ShortcutPolicy.FocusOf(new TerminalView()).Should().Be(ShortcutFocus.Terminal);
        ShortcutPolicy.FocusOf(new TextBox()).Should().Be(ShortcutFocus.TextInput);
        ShortcutPolicy.FocusOf(null).Should().Be(ShortcutFocus.Window);
    }

    [AvaloniaFact]
    public async Task SessionFocus_KeepsCtrlKeysForTheSession()
    {
        await ResetAsync();
        var window = TestHost.MainWindow;
        var factory = new FakeProtocolFactory();
        await SessionDock.OpenConnectionAsync(Connection("focus-test", 1), factory);
        Pump();
        var view = window.GetVisualDescendants().OfType<Border>()
            .First(b => b.Child is TextBlock { Text: "fake terminal" } && b.IsEffectivelyVisible);
        view.Focus();
        Pump();
        AppShortcuts.IsAllowed(window, KeyGesture.Parse("Ctrl+K")).Should().BeFalse();

        Press(window, PhysicalKey.K, RawInputModifiers.Control);
        Vm.Palette.IsOpen.Should().BeFalse("Ctrl+K goes to the session");
        Press(window, PhysicalKey.N, RawInputModifiers.Control);
        window.OwnedWindows.Should().BeEmpty("Ctrl+N goes to the session, no connection dialog");

        // In a text field, Delete edits the text instead of deleting the selected connection.
        var box = window.FindControl<TextBox>("QuickConnectHostBox")!;
        box.Focus();
        Pump();
        AppShortcuts.IsAllowed(window, KeyGesture.Parse("Delete")).Should().BeFalse();
        AppShortcuts.IsAllowed(window, KeyGesture.Parse("Ctrl+K")).Should().BeTrue();
        await ResetAsync();
    }

    // ── Command palette ───────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task CtrlK_OpensThePalette_AndEnterConnectsTheSelectedConnection()
    {
        await ResetAsync();
        using var server = new LoopbackServer();
        var window = TestHost.MainWindow;
        Tree.CreateNewTree();
        var target = Connection("palette-target", server.Port);
        Tree.AddConnection(Connection("unrelated", 1), Tree.Root);
        Tree.AddConnection(target, Tree.Root);
        FocusTree(window);

        Press(window, PhysicalKey.K, RawInputModifiers.Control);
        Vm.Palette.IsOpen.Should().BeTrue();
        await WaitUntil(() => window.FindControl<CommandPalette>("Palette")!.FindControl<TextBox>("QueryBox")!.IsFocused);
        Vm.Palette.Results.Should().Contain(i => i.Connection == target);
        Vm.Palette.Results.Should().Contain(i => i.IsCommand && i.Title == "Options" && i.Shortcut == "Ctrl+,");

        window.KeyTextInput("palette-tar");
        Pump();
        Vm.Palette.Selected!.Connection.Should().BeSameAs(target);

        Press(window, PhysicalKey.Enter);
        await WaitUntil(() => SessionDock.Sessions.Any(s => ReferenceEquals(s.Connection, target)));
        Vm.Palette.IsOpen.Should().BeFalse();
        Vm.Recent.Ids.First().Should().Be(target.ConstantID);

        // Ctrl+Shift+P toggles it too; Escape closes.
        FocusTree(window);
        Press(window, PhysicalKey.P, RawInputModifiers.Control | RawInputModifiers.Shift);
        Vm.Palette.IsOpen.Should().BeTrue();
        await WaitUntil(() => window.FindControl<CommandPalette>("Palette")!.FindControl<TextBox>("QueryBox")!.IsFocused);
        Press(window, PhysicalKey.Escape);
        Vm.Palette.IsOpen.Should().BeFalse();
        await ResetAsync();
    }

    [AvaloniaFact]
    public void PaletteCommands_RunTheMenuCommand()
    {
        var window = TestHost.MainWindow;
        var before = Vm.IsConnectionTreeVisible;
        Vm.Palette.Open();
        Vm.Palette.Query = "connection tree";
        Vm.Palette.Selected!.Title.Should().Be("Connection Tree");
        Vm.Palette.Selected.Detail.Should().Be("View");
        Vm.Palette.Selected.ShortcutKeys.Should().Equal("Ctrl", "Shift", "T");

        Vm.Palette.AcceptAsync().GetAwaiter().GetResult();
        Pump();
        Vm.IsConnectionTreeVisible.Should().Be(!before);
        Vm.IsConnectionTreeVisible = true;
        Pump();
        window.IsVisible.Should().BeTrue();
    }

    // ── Empty state, status bar, bottom panel ────────────────────────────

    [AvaloniaFact]
    public async Task EmptyState_ShowsRecentThenFavoriteCards()
    {
        await ResetAsync();
        var window = TestHost.MainWindow;
        Tree.CreateNewTree();
        var favorite = Connection("fav", 1);
        favorite.Favorite = true;
        var recent = Connection("recent", 1);
        Tree.AddConnection(favorite, Tree.Root);
        Tree.AddConnection(recent, Tree.Root);
        Tree.AddConnection(Connection("plain", 1), Tree.Root);
        Vm.Recent.Add(recent);
        Vm.RefreshHomeCards();
        Pump();

        Vm.HomeCards.Should().Equal(recent, favorite);
        var empty = window.FindControl<SessionsEmptyState>("EmptySessionsPanel")!;
        empty.IsVisible.Should().BeTrue();
        empty.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("conn-card")).Should().Be(2);
        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task StatusBar_CountsSessions_AndUnseenProblems()
    {
        await ResetAsync();
        var window = TestHost.MainWindow;
        var factory = new FakeProtocolFactory();
        await SessionDock.OpenConnectionAsync(Connection("one", 1), factory);
        await SessionDock.OpenConnectionAsync(Connection("two", 1), factory);
        Pump();
        Vm.HasSessions.Should().BeTrue();
        window.FindControl<TextBlock>("SessionCountText")!.Text.Should().Be("2 sessions");

        Vm.IsLogPanelVisible = true;
        Vm.BottomTab = BottomPanelTab.Debug;
        Vm.LogPanel.Log("careful", LogLevel.Warning);
        Vm.LogPanel.Log("plain info");
        Pump();
        Vm.UnseenLogProblems.Should().Be(1);
        Vm.UnseenLogHasErrors.Should().BeFalse();

        window.FindControl<Button>("LogIndicator")!.Command!.Execute(null);
        Pump();
        Vm.BottomTab.Should().Be(BottomPanelTab.Log);
        Vm.UnseenLogProblems.Should().Be(0, "the log is on screen now");
        await ResetAsync();
    }

    [AvaloniaFact]
    public void CtrlJ_CollapsesTheBottomPanelToItsHeader()
    {
        var window = TestHost.MainWindow;
        Vm.IsLogPanelVisible = true;
        Vm.IsBottomPanelExpanded = true;
        FocusTree(window);
        var grid = window.FindControl<Grid>("MainGrid")!;

        Press(window, PhysicalKey.J, RawInputModifiers.Control);
        Vm.IsBottomPanelExpanded.Should().BeFalse();
        grid.RowDefinitions[2].Height.IsAuto.Should().BeTrue();
        window.FindControl<Panel>("BottomContent")!.IsVisible.Should().BeFalse();

        Press(window, PhysicalKey.J, RawInputModifiers.Control);
        Vm.IsBottomPanelExpanded.Should().BeTrue();
        grid.RowDefinitions[2].Height.IsAbsolute.Should().BeTrue("the remembered height comes back");
    }

    [AvaloniaFact]
    public async Task NewPanel_AsksForTheName()
    {
        await ResetAsync();
        var window = TestHost.MainWindow;
        CloseOwnedWindows(window);

        Vm.NewPanelCommand.Execute().Subscribe();
        Pump();
        var prompt = window.OwnedWindows.OfType<TextPromptDialog>().Should().ContainSingle().Subject;
        prompt.Close("Operations");
        await WaitUntil(() => SessionDock.FindPanel("Operations") is not null);
        SessionDock.ActivePanel!.Name.Should().Be("Operations");
        await ResetAsync();
    }

    // ── Toasts ────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void LoggedErrors_BecomeToasts_InTheHost()
    {
        var window = TestHost.MainWindow;
        var toasts = AppServices.GetRequired<ToastService>();
        toasts.DismissAll();

        Vm.LogPanel.Log("Connection to 10.0.0.1:22 failed: refused", LogLevel.Error);
        Vm.LogPanel.Log("just a warning", LogLevel.Warning);
        Pump();

        toasts.Toasts.Should().ContainSingle().Which.Message.Should().Contain("refused");
        toasts.Toasts[0].HasAction.Should().BeTrue("errors offer \"Show log\"");
        window.FindControl<ToastHost>("Toasts")!.GetVisualDescendants().OfType<Border>()
            .Count(b => b.Classes.Contains("toast")).Should().Be(1);
        toasts.DismissAll();
    }
}
