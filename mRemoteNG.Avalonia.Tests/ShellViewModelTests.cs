using System.Reactive;
using System.Reactive.Disposables;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree.Root;
using ReactiveUI;
using Xunit;
using CoreProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>Command palette ranking, recent connections, toasts and quick connect parsing (no window).</summary>
public class ShellViewModelTests
{
    private static ConnectionInfo Conn(string name, string host = "", ContainerInfo? parent = null, string description = "")
    {
        var connection = new ConnectionInfo { Name = name, Hostname = host, Description = description, Protocol = CoreProtocolType.SSH2 };
        parent?.AddChild(connection);
        return connection;
    }

    private static PaletteCommand Command(string label, string category = "File") =>
        new(label, category, null, ReactiveCommand.Create(() => { }));

    private static IReadOnlyList<string> Titles(string query, IEnumerable<ConnectionInfo> connections,
        IReadOnlyList<ConnectionInfo>? recent = null, IEnumerable<PaletteCommand>? commands = null) =>
        PaletteSearch.Search(query, connections, recent ?? [], commands ?? []).Select(i => i.Title).ToList();

    // ── Palette ranking ───────────────────────────────────────────────────

    [Fact]
    public void EmptyQuery_ListsRecentThenFavoritesThenOthersThenCommands()
    {
        var a = Conn("alpha");
        var b = Conn("bravo");
        var c = Conn("charlie");
        c.Favorite = true;
        var d = Conn("delta");

        Titles("", [a, b, c, d], recent: [d, b], commands: [Command("Options")])
            .Should().Equal("delta", "bravo", "charlie", "alpha", "Options");
    }

    [Fact]
    public void NameMatches_RankByQuality_AndBeatHostMatches()
    {
        var exact = Conn("web");
        var prefix = Conn("webserver");
        var wordStart = Conn("prod web");
        var inside = Conn("cobweb");
        var host = Conn("database", host: "web.example.com");
        var fuzzy = Conn("w-e-b gateway");

        Titles("web", [fuzzy, host, inside, wordStart, prefix, exact])
            .Should().Equal("web", "webserver", "prod web", "database", "cobweb", "w-e-b gateway");
    }

    [Fact]
    public void EveryWord_MustMatch_SomeField()
    {
        var root = new RootNodeInfo(Core.Tree.Root.RootNodeType.Connection);
        var linux = new ContainerInfo { Name = "Linux lab" };
        root.AddChild(linux);
        var ssh = Conn("ssh demo", "10.0.0.5", linux);
        var other = Conn("ssh prod", "10.0.0.9", root);

        Titles("ssh linux", [ssh, other]).Should().Equal("ssh demo");
        Titles("10.0.0.9", [ssh, other]).Should().Equal("ssh prod");
        PaletteSearch.FolderPath(ssh).Should().Be("Linux lab");
        Titles("backup", [Conn("nas", description: "nightly backup target"), other]).Should().Equal("nas");
        Titles("zzz", [ssh, other]).Should().BeEmpty();
    }

    [Fact]
    public void RecentConnections_WinTies_AndCommandsMatchTheirLabel()
    {
        var older = Conn("server one");
        var newer = Conn("server two");

        Titles("server", [older, newer], recent: [newer]).Should().Equal("server two", "server one");
        Titles("save", [older], commands: [Command("Save Connection File"), Command("Options", "Tools")])
            .Should().Equal("Save Connection File");
        Titles("tools", [older], commands: [Command("Options", "Tools")]).Should().ContainSingle()
            .Which.Should().Be("Options", "the menu name matches too");
    }

    [AvaloniaFact]
    public async Task Palette_NavigatesWithWrap_AndRunsCommandsOrConnects()
    {
        var a = Conn("alpha");
        var b = Conn("bravo");
        ConnectionInfo? connected = null;
        var withOptions = false;
        var ran = 0;
        var palette = new CommandPaletteViewModel(() => [a, b], () => [])
        {
            Commands = () => [new PaletteCommand("Run me", "Tools", "Ctrl+Shift+R", ReactiveCommand.Create(() => { ran++; }))],
            Connect = (connection, options) =>
            {
                connected = connection;
                withOptions = options;
                return Task.CompletedTask;
            },
        };

        palette.Open();
        palette.IsOpen.Should().BeTrue();
        palette.Selected!.Title.Should().Be("alpha");
        palette.MoveSelection(-1);
        palette.Selected!.Title.Should().Be("Run me", "Up from the first row wraps to the last");
        palette.Selected.ShortcutKeys.Should().Equal("Ctrl", "Shift", "R");
        palette.MoveSelection(+1);
        palette.Selected!.Title.Should().Be("alpha");

        await palette.AcceptAsync(withOptions: true);
        palette.IsOpen.Should().BeFalse();
        connected.Should().BeSameAs(a);
        withOptions.Should().BeTrue();

        palette.Open();
        palette.Query = "run";
        await palette.AcceptAsync();
        ran.Should().Be(1);
    }

    // ── Recent connections ────────────────────────────────────────────────

    [Fact]
    public void Recent_KeepsTheMostRecentFirst_AndRoundTrips()
    {
        var root = new RootNodeInfo(Core.Tree.Root.RootNodeType.Connection);
        var connections = Enumerable.Range(0, RecentConnections.Capacity + 2).Select(i => Conn($"c{i}", parent: root)).ToList();
        var recent = new RecentConnections();
        var changes = 0;
        recent.Changed += (_, _) => changes++;

        foreach (var connection in connections)
            recent.Add(connection);
        recent.Add(connections[3]);
        recent.Add(connections[3]);
        recent.Add(new ContainerInfo { Name = "folder" });

        recent.Ids.Should().HaveCount(RecentConnections.Capacity);
        recent.Ids[0].Should().Be(connections[3].ConstantID);
        changes.Should().Be(connections.Count + 1, "re-adding the first one or a folder changes nothing");
        recent.Resolve(root).Take(2).Should().Equal(connections[3], connections[^1]);

        var copy = new RecentConnections();
        copy.Load(recent.Serialize());
        copy.Ids.Should().Equal(recent.Ids);
        copy.Resolve(new RootNodeInfo(Core.Tree.Root.RootNodeType.Connection)).Should().BeEmpty("other files have other ids");
    }

    // ── Toasts ────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Toasts_StackAtMostThree_AutoDismissExceptErrors_AndDeduplicate()
    {
        var scheduled = new List<(TimeSpan Delay, Action Run)>();
        var toasts = new ToastService((delay, run) =>
        {
            scheduled.Add((delay, run));
            return Disposable.Empty;
        });

        toasts.Show("Saved a.xml", level: ToastLevel.Success);
        toasts.Show("Boom", "details", ToastLevel.Error);
        scheduled.Should().ContainSingle().Which.Delay.Should().Be(ToastService.DefaultDuration, "errors stay until closed");

        toasts.Show("Third");
        toasts.Show("Fourth");
        toasts.Toasts.Select(t => t.Title).Should().Equal("Boom", "Third", "Fourth");

        toasts.Show("Boom", "details", ToastLevel.Error);
        toasts.Toasts.Select(t => t.Title).Should().Equal("Third", "Fourth", "Boom");

        scheduled[1].Run();
        toasts.Toasts.Select(t => t.Title).Should().Equal("Fourth", "Boom");
        toasts.Toasts[1].DismissCommand.Execute().Subscribe();
        toasts.Toasts.Select(t => t.Title).Should().Equal("Fourth");
    }

    [AvaloniaFact]
    public void Toasts_FollowLoggedErrors_WithAShowLogAction()
    {
        var log = new LogPanelDockable();
        var toasts = new ToastService((_, _) => Disposable.Empty);
        var shown = 0;
        toasts.AttachLog(log, () => shown++);

        log.Log("fine");
        log.Log("hmm", LogLevel.Warning);
        log.Log("Connection to db:5432 failed: timeout", LogLevel.Error);

        var toast = toasts.Toasts.Should().ContainSingle().Subject;
        toast.IsError.Should().BeTrue();
        toast.ActionCommand.Execute().Subscribe();
        shown.Should().Be(1);
        toasts.Toasts.Should().BeEmpty();
    }

    // ── Quick connect field ───────────────────────────────────────────────

    [Theory]
    [InlineData("server01", CoreProtocolType.SSH2, "server01", CoreProtocolType.SSH2, null)]
    [InlineData("rdp://win01:3390", CoreProtocolType.SSH2, "win01:3390", CoreProtocolType.RDP, null)]
    [InlineData("admin@10.0.0.1:2222", CoreProtocolType.SSH2, "10.0.0.1:2222", CoreProtocolType.SSH2, "admin")]
    [InlineData("ssh://root@host/", CoreProtocolType.VNC, "host", CoreProtocolType.SSH2, "root")]
    [InlineData("https://intranet/status", CoreProtocolType.SSH2, "https://intranet/status", CoreProtocolType.HTTPS, null)]
    [InlineData("vnc://desk:5901", CoreProtocolType.SSH2, "desk:5901", CoreProtocolType.VNC, null)]
    public void QuickConnectInput_PicksProtocolAndUser(string input, CoreProtocolType selected, string host, CoreProtocolType protocol, string? user)
    {
        MainWindowViewModel.ParseQuickConnectInput(input, selected).Should().Be((host, protocol, user));
    }
}
