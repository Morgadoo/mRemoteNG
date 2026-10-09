using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Connection;
using mRemoteNG.Protocols.Abstractions;
using Xunit;
using static mRemoteNG.Avalonia.Tests.SessionTestSupport;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>PleaseConnect bookkeeping, reopening sessions at startup, auto-reconnect, favorites and Multi-SSH.</summary>
public class SessionLifecycleTests
{
    private static ViewModels.ConnectionTreeViewModel Tree => TestHost.ViewModel.ConnectionTree;

    private static string? ConnectedAttribute(string file, string name) =>
        XDocument.Load(file).Descendants("Node").Single(n => (string?)n.Attribute("Name") == name).Attribute("Connected")?.Value;

    [AvaloniaFact]
    public async Task PleaseConnect_FollowsOpenSessions_AndIsWrittenToTheFile()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        Tree.CreateNewTree();
        var web = Connection("web", 1);
        var db = Connection("db", 1);
        Tree.AddConnection(web, Tree.Root);
        Tree.AddConnection(db, Tree.Root);
        var file = Path.Combine(TestHost.ConfigDirectory, $"pleaseconnect-{Guid.NewGuid():N}.xml");

        try
        {
            var first = (await SessionDock.OpenConnectionAsync(web, factory))!;
            var second = (await SessionDock.DuplicateSessionAsync(first))!;
            web.PleaseConnect.Should().BeTrue();
            db.PleaseConnect.Should().BeFalse();

            Tree.SaveToFile(file);
            ConnectedAttribute(file, "web").Should().Be("true", "legacy writes Connected for nodes with open sessions");
            ConnectedAttribute(file, "db").Should().Be("false");

            await SessionDock.CloseSessionAsync(first);
            web.PleaseConnect.Should().BeTrue("another session of the node is still open");
            await SessionDock.CloseSessionAsync(second);
            web.PleaseConnect.Should().BeFalse();

            Tree.SaveToFile(file);
            ConnectedAttribute(file, "web").Should().Be("false");

            // Quick connects are not in the tree and are never flagged.
            var quick = Connection("quick", 1);
            quick.IsQuickConnect = true;
            await SessionDock.OpenConnectionAsync(quick, factory);
            quick.PleaseConnect.Should().BeFalse();
        }
        finally
        {
            File.Delete(file);
            Tree.CreateNewTree();
            await ResetAsync();
        }
    }

    [AvaloniaFact]
    public async Task ReconnectAtStartup_OpensTheSessionsSavedAsConnected()
    {
        await ResetAsync();
        using var server = new LoopbackServer();
        Tree.CreateNewTree();
        var reopen = Connection("reopen-me", server.Port, panel: "Startup");
        reopen.PleaseConnect = true;
        Tree.AddConnection(reopen, Tree.Root);
        Tree.AddConnection(Connection("leave-me", server.Port), Tree.Root);
        var file = Path.Combine(TestHost.ConfigDirectory, $"startup-{Guid.NewGuid():N}.xml");
        Tree.SaveToFile(file);

        try
        {
            // Option off: nothing is reopened.
            Tree.LoadFromFile(file);
            TestHost.ViewModel.OpenPreviousSessions().Should().Be(0);

            Settings.Update(s => s.OpenConnectionsFromLastSession = true);
            Tree.LoadFromFile(file);
            TestHost.ViewModel.OpenPreviousSessions().Should().Be(1);
            await WaitUntil(() => SessionDock.Sessions.Count == 1 && server.Accepted == 1);
            var session = SessionDock.Sessions.Single();
            session.Connection!.Name.Should().Be("reopen-me");
            session.Panel!.Name.Should().Be("Startup", "the session opens in the connection's panel");

            // Sessions already open are not opened twice.
            TestHost.ViewModel.OpenPreviousSessions().Should().Be(0);
        }
        finally
        {
            File.Delete(file);
            Tree.CreateNewTree();
            await ResetAsync();
        }
    }

    [AvaloniaFact]
    public async Task AutoReconnect_RetriesWithBackoff_AfterAnUnexpectedDrop()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        SessionDock.AutoReconnectBaseDelay = TimeSpan.FromMilliseconds(20);
        Settings.Update(s =>
        {
            s.ReconnectOnDisconnect = true;
            s.ReconnectAttempts = 4;
        });

        var tab = (await SessionDock.OpenConnectionAsync(Connection("flaky", 1), factory))!;
        tab.IsConnected.Should().BeTrue();

        // The server goes away; the first retry fails, the second one connects.
        factory.FailNext = 1;
        factory.Last.Drop();
        Pump();
        tab.ReconnectStatus.Should().NotBeNull("the tab shows the reconnect status");
        tab.Title.Should().StartWith("↺");
        await WaitUntil(() => tab.IsConnected && tab.ReconnectStatus is null);
        factory.Created.Should().HaveCount(3, "the original plus two attempts");
        factory.Created[0].Disposed.Should().BeTrue();
        tab.Protocol.Should().BeSameAs(factory.Last);

        // A disconnect the user asked for is not retried.
        await SessionDock.DisconnectAllAsync();
        Pump();
        await Task.Delay(100);
        Pump();
        SessionDock.IsAutoReconnecting(tab).Should().BeFalse();
        factory.Created.Should().HaveCount(3);

        // Reconnect from the tab, then give up after the configured attempts.
        await SessionDock.ReconnectSessionAsync(tab);
        tab.IsConnected.Should().BeTrue();
        factory.FailNext = 10;
        factory.Last.Drop();
        await WaitUntil(() => tab.ReconnectStatus?.StartsWith("Could not reconnect", StringComparison.Ordinal) == true);
        factory.Created.Should().HaveCount(4 + 4, "four tries after the drop");
        SessionDock.AutoReconnectDelay(1).Should().Be(TimeSpan.FromMilliseconds(20));
        SessionDock.AutoReconnectDelay(3).Should().Be(TimeSpan.FromMilliseconds(80));

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task AutoReconnect_IsOffByDefault_AndStopsWhenTheTabCloses()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var tab = (await SessionDock.OpenConnectionAsync(Connection("off", 1), factory))!;
        factory.Last.Drop();
        Pump();
        SessionDock.IsAutoReconnecting(tab).Should().BeFalse();
        tab.ShowStatusBanner.Should().BeTrue("the drop is shown in the session");

        SessionDock.AutoReconnectBaseDelay = TimeSpan.FromSeconds(5);
        Settings.Update(s => s.ReconnectOnDisconnect = true);
        await SessionDock.ReconnectSessionAsync(tab);
        factory.Last.Drop();
        Pump();
        SessionDock.IsAutoReconnecting(tab).Should().BeTrue();
        await SessionDock.CloseSessionAsync(tab);
        SessionDock.IsAutoReconnecting(tab).Should().BeFalse();
        var created = factory.Created.Count;
        await Task.Delay(50);
        Pump();
        factory.Created.Should().HaveCount(created);

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task FavoritesMenu_ListsFavoriteConnections_AndConnects()
    {
        await ResetAsync();
        using var server = new LoopbackServer();
        Tree.CreateNewTree();
        var fav = Connection("fav-one", server.Port);
        fav.Favorite = true;
        Tree.AddConnection(fav, Tree.Root);
        Tree.AddConnection(Connection("plain", server.Port), Tree.Root);

        try
        {
            TestHost.ViewModel.GetFavorites().Should().ContainSingle().Which.Should().BeSameAs(fav);
            TestHost.MainWindow.RebuildFavorites();
            var menu = TestHost.MainWindow.FindControl<MenuItem>("FavoritesMenu")!;
            var item = menu.Items.OfType<MenuItem>().Should().ContainSingle().Subject;
            item.Header.Should().Be("fav-one");

            item.RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            await WaitUntil(() => SessionDock.Sessions.Count == 1 && server.Accepted == 1);
            SessionDock.Sessions.Single().Connection.Should().BeSameAs(fav);

            fav.Favorite = false;
            TestHost.MainWindow.RebuildFavorites();
            menu.Items.OfType<MenuItem>().Single().IsEnabled.Should().BeFalse("only the \"(No favorites)\" placeholder remains");
        }
        finally
        {
            Tree.CreateNewTree();
            await ResetAsync();
        }
    }

    [AvaloniaFact]
    public async Task MultiSsh_SendsToEveryConnectedTerminalTarget()
    {
        await ResetAsync();
        var factory = new FakeProtocolFactory();
        var one = (await SessionDock.OpenConnectionAsync(Connection("one", 1), factory))!;
        var two = (await SessionDock.OpenConnectionAsync(Connection("two", 1), factory))!;
        var other = (await SessionDock.OpenConnectionAsync(Connection("other-panel", 1, panel: "Other"), factory))!;
        var excluded = (await SessionDock.OpenConnectionAsync(Connection("excluded", 1), factory))!;
        excluded.IsMultiSshTarget = false;
        var dropped = (await SessionDock.OpenConnectionAsync(Connection("dropped", 1), factory))!;
        ((FakeTerminalProtocol)dropped.Protocol).Drop();
        var plain = new SessionTabViewModel(new FakePlainProtocol(), new ConnectionParameters { Hostname = "rdp", Port = 3389, Protocol = ProtocolType.Rdp });
        SessionDock.AddSession(plain);
        await plain.ConnectAsync();
        Pump();

        var multi = TestHost.ViewModel.MultiSsh;
        multi.GetTargets().Should().BeEquivalentTo(new[] { one, two, other });
        multi.TargetSummary.Should().Be("3 sessions");

        multi.CommandText = "uptime";
        (await multi.SendCommandAsync()).Should().Be(3);
        foreach (var target in new[] { one, two, other })
            ((FakeTerminalProtocol)target.Protocol).Received.Should().Equal("uptime\r");
        ((FakeTerminalProtocol)excluded.Protocol).Received.Should().BeEmpty();
        multi.CommandText.Should().BeEmpty();

        // Ctrl+C goes out as ETX; history recalls the last command.
        (await multi.SendControlAsync('c')).Should().Be(3);
        ((FakeTerminalProtocol)one.Protocol).Received.Last().Should().Be("\u0003");
        multi.NavigateHistory(-1).Should().BeTrue();
        multi.CommandText.Should().Be("uptime");

        // Active panel only.
        SessionDock.ActiveSession = one;
        multi.Scope = MultiSshScope.ActivePanel;
        multi.GetTargets().Should().BeEquivalentTo(new[] { one, two });
        multi.Scope = MultiSshScope.AllSessions;

        // Through the toolbar's text box: Enter sends.
        var box = TestHost.MainWindow.FindControl<TextBox>("MultiSshBox")!;
        multi.CommandText = "whoami";
        box.RaiseEvent(new global::Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = global::Avalonia.Input.InputElement.KeyDownEvent,
            Key = global::Avalonia.Input.Key.Enter,
            Source = box,
        });
        await WaitUntil(() => ((FakeTerminalProtocol)two.Protocol).Received.Contains("whoami\r"));

        await ResetAsync();
    }
}
