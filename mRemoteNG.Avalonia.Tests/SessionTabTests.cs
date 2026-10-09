using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using FluentAssertions;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views.Sessions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Protocols.Abstractions;
using Xunit;
using static mRemoteNG.Avalonia.Tests.SessionTestSupport;

// The tests share one main window and settings; run them one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace mRemoteNG.Avalonia.Tests;

/// <summary>The session tab context menu, navigation and tab appearance.</summary>
public class SessionTabTests
{
    private static IProtocolFactory RealFactory => AppServices.GetRequired<IProtocolFactory>();

    [AvaloniaFact]
    public async Task TabMenu_Duplicate_Reconnect_And_CloseOthers_WithRawSessions()
    {
        await ResetAsync();
        using var server = new LoopbackServer();
        var connection = Connection("raw-host", server.Port);

        var first = await SessionDock.OpenConnectionAsync(connection, RealFactory);
        first.Should().NotBeNull();
        await WaitUntil(() => server.Accepted == 1 && first!.IsConnected);

        // Duplicate: same connection, options and panel.
        var menu = SessionTabMenu.Build(first!, TestHost.MainWindow);
        SessionTabMenu.Invoke(SessionTabMenu.Find(menu, SessionTabMenu.Duplicate)!);
        await WaitUntil(() => SessionDock.Sessions.Count == 2 && server.Accepted == 2);
        var duplicate = SessionDock.Sessions.Single(s => !ReferenceEquals(s, first));
        duplicate.Connection.Should().BeSameAs(connection);
        duplicate.Panel.Should().BeSameAs(first!.Panel);
        await WaitUntil(() => duplicate.IsConnected);

        // Reconnect: a new protocol instance connects in the same tab.
        var oldProtocol = first.Protocol;
        menu = SessionTabMenu.Build(first, TestHost.MainWindow);
        SessionTabMenu.Invoke(SessionTabMenu.Find(menu, SessionTabMenu.Reconnect)!);
        await WaitUntil(() => server.Accepted == 3 && !ReferenceEquals(first.Protocol, oldProtocol) && first.IsConnected);
        SessionDock.Sessions.Should().Contain(first);

        // Close other tabs keeps only this one.
        menu = SessionTabMenu.Build(first, TestHost.MainWindow);
        SessionTabMenu.Invoke(SessionTabMenu.Find(menu, SessionTabMenu.CloseOthers)!);
        await WaitUntil(() => SessionDock.Sessions.Count == 1);
        SessionDock.Sessions.Should().ContainSingle().Which.Should().BeSameAs(first);

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task TabMenu_DisablesWhatTheProtocolCannotDo()
    {
        await ResetAsync();
        using var server = new LoopbackServer();
        var raw = await SessionDock.OpenConnectionAsync(Connection("raw", server.Port), RealFactory);
        await WaitUntil(() => raw!.IsConnected);

        var menu = SessionTabMenu.Build(raw!, TestHost.MainWindow);
        SessionTabMenu.Find(menu, SessionTabMenu.SpecialKeys)!.IsEnabled.Should().BeFalse("RAW has no ISpecialKeysProtocol");
        SessionTabMenu.Find(menu, SessionTabMenu.SmartSize)!.IsEnabled.Should().BeFalse();
        SessionTabMenu.Find(menu, SessionTabMenu.ViewOnly)!.IsEnabled.Should().BeFalse();
        SessionTabMenu.Find(menu, SessionTabMenu.TransferFile)!.IsEnabled.Should().BeFalse("SFTP is for SSH sessions");
        SessionTabMenu.Find(menu, SessionTabMenu.Reconnect)!.IsEnabled.Should().BeTrue();
        SessionTabMenu.Find(menu, SessionTabMenu.FullScreen)!.IsEnabled.Should().BeTrue();
        SessionTabMenu.Find(menu, SessionTabMenu.CopyHostname)!.IsEnabled.Should().BeTrue();
        SessionTabMenu.Find(menu, SessionTabMenu.CloseOthers)!.IsEnabled.Should().BeFalse("it is the only tab");

        // A protocol with the capability interfaces enables them.
        var special = new SpecialProtocol();
        var tab = new SessionTabViewModel(special, new ConnectionParameters { Hostname = "vnc", Port = 5900, Protocol = ProtocolType.Vnc });
        SessionDock.AddSession(tab);
        await tab.ConnectAsync();
        menu = SessionTabMenu.Build(tab, TestHost.MainWindow);
        var keys = SessionTabMenu.Find(menu, SessionTabMenu.SpecialKeys)!;
        keys.IsEnabled.Should().BeTrue();
        SessionTabMenu.Invoke(SessionTabMenu.Find(menu, SessionTabMenu.CtrlAltDel)!);
        await WaitUntil(() => special.Sent.Count == 1);
        special.Sent.Should().Equal(SpecialKey.CtrlAltDel);
        SessionTabMenu.Find(menu, SessionTabMenu.CtrlEsc)!.IsEnabled.Should().BeFalse("only Ctrl+Alt+Del is supported");

        var smart = SessionTabMenu.Find(menu, SessionTabMenu.SmartSize)!;
        smart.IsEnabled.Should().BeTrue();
        smart.IsChecked.Should().BeFalse();
        SessionTabMenu.Invoke(smart);
        special.SmartSize.Should().BeTrue();
        SessionTabMenu.Find(menu, SessionTabMenu.ViewOnly)!.IsEnabled.Should().BeFalse();

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task CloseToTheRight_And_Rename()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var tabs = new List<SessionTabViewModel>();
        foreach (var name in new[] { "a", "b", "c", "d" })
            tabs.Add((await SessionDock.OpenConnectionAsync(Connection(name, 1), factory))!);

        await SessionDock.CloseSessionsToTheRightAsync(tabs[1]);
        SessionDock.Sessions.Should().Equal(tabs[0], tabs[1]);
        factory.Created[2].Disposed.Should().BeTrue();

        tabs[0].CustomTitle = "My server";
        tabs[0].DisplayTitle.Should().Be("My server");
        tabs[0].Title.Should().EndWith("My server");
        tabs[0].CustomTitle = "";
        tabs[0].DisplayTitle.Should().Be("a");

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task CtrlTab_And_CtrlNumber_NavigateTheActivePanel()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var a = (await SessionDock.OpenConnectionAsync(Connection("a", 1), factory))!;
        var b = (await SessionDock.OpenConnectionAsync(Connection("b", 1), factory))!;
        var c = (await SessionDock.OpenConnectionAsync(Connection("c", 1), factory))!;
        SessionDock.ActiveSession.Should().BeSameAs(c);

        bool Press(Key key, KeyModifiers modifiers) =>
            SessionKeyboard.Handle(SessionDock, new KeyEventArgs { Key = key, KeyModifiers = modifiers });

        Press(Key.Tab, KeyModifiers.Control).Should().BeTrue();
        SessionDock.ActiveSession.Should().BeSameAs(a, "Ctrl+Tab wraps around");
        Press(Key.Tab, KeyModifiers.Control | KeyModifiers.Shift);
        SessionDock.ActiveSession.Should().BeSameAs(c);
        Press(Key.D2, KeyModifiers.Control).Should().BeTrue();
        SessionDock.ActiveSession.Should().BeSameAs(b);
        Press(Key.D9, KeyModifiers.Control).Should().BeFalse("there is no ninth tab");

        // The Sessions menu lists the open sessions with Ctrl+n.
        TestHost.MainWindow.RebuildSessionList();
        var sessionsMenu = TestHost.MainWindow.FindControl<MenuItem>("SessionsMenu")!;
        var listed = sessionsMenu.Items.OfType<MenuItem>().Where(i => i.InputGesture is not null && i.ToggleType == MenuItemToggleType.Radio).ToList();
        listed.Select(i => i.Header).Should().Equal("a", "b", "c");
        listed[1].IsChecked.Should().BeTrue();

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task TabAppearance_FollowsConnectionAndOptions()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var info = Connection("web01", 22);
        info.TabColor = "Red";
        info.ConnectionFrameColor = ConnectionFrameColor.Green;
        info.EnvironmentTags = "prod, eu";
        info.Username = "admin";
        info.Domain = "CORP";
        var tab = (await SessionDock.OpenConnectionAsync(info, factory))!;

        tab.DisplayTitle.Should().Be("web01");
        ((ISolidColorBrush)tab.TabColorBrush!).Color.Should().Be(Colors.Red);
        ((ISolidColorBrush)tab.FrameBrush).Color.Should().Be(Color.FromRgb(40, 167, 69));
        tab.FrameThickness.Left.Should().Be(SessionTabAppearance.FrameWidth);
        tab.EnvironmentTags.Select(t => t.Text).Should().Equal("prod", "eu");
        ((ISolidColorBrush)tab.EnvironmentTags[0].Background).Color.Should().Be(Color.FromRgb(198, 40, 40));
        tab.Icon.Should().NotBeNull();

        Settings.Update(s =>
        {
            s.ShowProtocolOnTabs = true;
            s.ShowLogonInfoOnTabs = true;
        });
        Pump();
        tab.DisplayTitle.Should().Be(@"RAW: web01 (CORP\admin)");

        info.Name = "web02";
        Pump();
        tab.DisplayTitle.Should().Be(@"RAW: web02 (CORP\admin)", "the tab follows the connection");

        // The tab view shows the frame around the session.
        var host = TestHost.MainWindow.GetVisualDescendantsOfType<SessionContentHost>().Single(h => ReferenceEquals(h.Session, tab));
        host.HostedView.Should().BeSameAs(tab.ContentView);

        await ResetAsync();
    }

    [AvaloniaFact]
    public void TabColors_ParseLegacyFormats()
    {
        SessionTabAppearance.ParseTabColor("Red").Should().Be(Colors.Red);
        SessionTabAppearance.ParseTabColor("#FF8000").Should().Be(Color.FromRgb(255, 128, 0));
        SessionTabAppearance.ParseTabColor("Color [Blue]").Should().Be(Colors.Blue);
        SessionTabAppearance.ParseTabColor("Color [A=255, R=1, G=2, B=3]").Should().Be(Color.FromRgb(1, 2, 3));
        SessionTabAppearance.ParseTabColor("").Should().BeNull();
        SessionTabAppearance.ParseTabColor("not a colour").Should().BeNull();
        SessionTabAppearance.FrameColor(ConnectionFrameColor.None).Should().BeNull();
        SessionTabAppearance.FrameColor(ConnectionFrameColor.Purple).Should().Be(Color.FromRgb(111, 66, 193));
    }

    [AvaloniaFact]
    public async Task FullScreen_MovesTheViewToItsOwnWindowAndBack()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var tab = (await SessionDock.OpenConnectionAsync(Connection("fs", 1), factory))!;
        Pump();
        var host = TestHost.MainWindow.GetVisualDescendantsOfType<SessionContentHost>().Single(h => ReferenceEquals(h.Session, tab));

        var window = SessionFullScreenWindow.Enter(tab, TestHost.MainWindow);
        Pump();
        tab.IsDetached.Should().BeTrue();
        window.HostedView.Should().BeSameAs(tab.ContentView);
        host.HostedView.Should().BeNull("a control has only one parent");
        SessionFullScreenWindow.IsFullScreen(tab).Should().BeTrue();

        SessionFullScreenWindow.Exit(tab);
        Pump();
        tab.IsDetached.Should().BeFalse();
        host.HostedView.Should().BeSameAs(tab.ContentView);

        // Closing the session closes its full-screen window.
        window = SessionFullScreenWindow.Enter(tab, TestHost.MainWindow);
        await SessionDock.CloseSessionAsync(tab);
        Pump();
        SessionFullScreenWindow.IsFullScreen(tab).Should().BeFalse();
        window.IsVisible.Should().BeFalse();

        await ResetAsync();
    }

    private sealed class SpecialProtocol : ProtocolBase, ISpecialKeysProtocol, IDisplayOptionsProtocol
    {
        public List<SpecialKey> Sent { get; } = [];

        public IReadOnlyList<SpecialKey> SupportedSpecialKeys { get; } = [SpecialKey.CtrlAltDel];

        public bool SupportsSmartSize => true;

        public bool SmartSize { get; set; }

        public bool SupportsViewOnly => false;

        public bool ViewOnly { get; set; }

        public Task SendSpecialKeyAsync(SpecialKey key, CancellationToken ct = default)
        {
            Sent.Add(key);
            return Task.CompletedTask;
        }

        public override Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
        {
            State = ConnectionState.Connected;
            return Task.CompletedTask;
        }

        public override Task DisconnectAsync(CancellationToken ct = default)
        {
            State = ConnectionState.Disconnected;
            return Task.CompletedTask;
        }
    }
}

internal static class VisualExtensions
{
    public static IEnumerable<T> GetVisualDescendantsOfType<T>(this global::Avalonia.Visual visual) =>
        global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(visual).OfType<T>();
}
