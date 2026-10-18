using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Avalonia.Views.Sessions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;
using Xunit;
using static mRemoteNG.Avalonia.Tests.SessionTestSupport;

namespace mRemoteNG.Avalonia.Tests;

/// <summary>The UltraVNC chat window and file browser opened from a session tab.</summary>
public class SessionChatAndFileTransferTests
{
    [AvaloniaFact]
    public async Task Chat_OpensFromTheTabMenu_SendsAndReceives_AndClosesOnTheServer()
    {
        await ResetAsync();
        var protocol = new FakeVncProtocol();
        var tab = await AddVncTabAsync(protocol);

        var menu = SessionTabMenu.Build(tab, TestHost.MainWindow);
        var item = SessionTabMenu.Find(menu, SessionTabMenu.StartChat)!;
        item.IsEnabled.Should().BeTrue();
        SessionTabMenu.Invoke(item);
        var chat = tab.TextChat!;
        await WaitUntil(() => TextChatWindow.Find(chat) is { IsVisible: true });
        protocol.ChatCalls.Should().Equal("open");
        chat.Title.Should().Contain("vnc-host");

        chat.Draft = "hello";
        await chat.SendCommand.Execute();
        protocol.ChatCalls.Should().Equal("open", "send:hello");
        chat.Draft.Should().BeEmpty();
        chat.Lines.Should().ContainSingle(l => l.IsLocal && l.Text == "hello");

        protocol.Raise(new TextChatEvent(TextChatEventKind.Message, "hi back"));
        await WaitUntil(() => chat.Lines.Any(l => l.IsRemote && l.Text == "hi back"));
        chat.Lines.Last().Sender.Should().Be("vnc-host:5900");

        // Opening it again brings the same window to the front.
        SessionTabMenu.Invoke(SessionTabMenu.Find(SessionTabMenu.Build(tab, TestHost.MainWindow), SessionTabMenu.StartChat)!);
        Pump();
        protocol.ChatCalls.Should().HaveCount(2);

        TextChatWindow.Find(chat)!.Close();
        Pump();
        TextChatWindow.Find(chat).Should().BeNull();
        protocol.ChatCalls.Should().Equal("open", "send:hello", "close");

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task Chat_StartedByTheServer_OpensTheWindow_WhichClosesWithTheSession()
    {
        await ResetAsync();
        var protocol = new FakeVncProtocol();
        var tab = await AddVncTabAsync(protocol);
        var chat = tab.TextChat!;

        protocol.Raise(new TextChatEvent(TextChatEventKind.Opened));
        await WaitUntil(() => TextChatWindow.Find(chat) is { IsVisible: true });
        protocol.ChatCalls.Should().BeEmpty("the server already opened the chat");
        chat.Lines.Should().ContainSingle(l => l.IsNotice);

        protocol.Raise(new TextChatEvent(TextChatEventKind.Closed));
        protocol.Raise(new TextChatEvent(TextChatEventKind.Closed));
        await WaitUntil(() => chat.Lines.Count == 2);
        Pump();
        chat.Lines.Should().HaveCount(2, "closed and finished give one notice");

        await SessionDock.CloseSessionAsync(tab);
        await WaitUntil(() => TextChatWindow.Find(chat) is null);

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task Chat_SurvivesAReconnect_AndFollowsTheNewProtocol()
    {
        await ResetAsync();
        var factory = new FakeVncFactory();
        var first = (FakeVncProtocol)factory.Create(ProtocolType.Vnc);
        var tab = await AddVncTabAsync(first, factory);
        var chat = tab.TextChat!;

        await SessionDock.ReconnectSessionAsync(tab);
        await WaitUntil(() => factory.Created.Count == 2 && tab.IsConnected);
        var second = factory.Created[1];
        tab.Protocol.Should().BeSameAs(second);
        tab.TextChat.Should().BeSameAs(chat);

        first.Raise(new TextChatEvent(TextChatEventKind.Message, "stale"));
        second.Raise(new TextChatEvent(TextChatEventKind.Message, "fresh"));
        await WaitUntil(() => chat.Lines.Count > 0);
        Pump();
        chat.Lines.Select(l => l.Text).Should().Equal("fresh");

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task UltraVncFileTransfer_WaitsForConnect_ThenBrowsesOverTheSession()
    {
        await ResetAsync();
        var protocol = new FakeVncProtocol();
        var tab = await AddVncTabAsync(protocol);

        var menu = SessionTabMenu.Build(tab, TestHost.MainWindow);
        SessionTabMenu.Find(menu, SessionTabMenu.TransferFile)!.IsEnabled.Should().BeFalse("SFTP is for SSH sessions");
        var item = SessionTabMenu.Find(menu, SessionTabMenu.TransferFileUltraVnc)!;
        item.IsEnabled.Should().BeTrue();
        SessionTabMenu.Invoke(item);
        await WaitUntil(() => TestHost.MainWindow.OwnedWindows.OfType<SshFileTransferDialog>().Any(d => d.IsVisible));
        var dialog = TestHost.MainWindow.OwnedWindows.OfType<SshFileTransferDialog>().Single();
        var viewModel = (SshFileTransferViewModel)dialog.DataContext!;
        viewModel.IsAttached.Should().BeTrue();
        viewModel.Title.Should().NotContain("SFTP");
        Pump();
        protocol.FileSessionsOpened.Should().Be(0, "LibVNCServer servers drop the session, so the user connects explicitly");

        await viewModel.ConnectCommand.Execute();
        protocol.FileSessionsOpened.Should().Be(1);
        viewModel.IsConnected.Should().BeTrue(viewModel.Status);
        viewModel.RemotePath.Should().Be(@"C:\");
        viewModel.RemoteFiles.Select(f => f.Name).Should().Equal("Users/", "boot.ini");

        await viewModel.OpenAsync(viewModel.RemoteFiles[0]);
        viewModel.RemotePath.Should().Be(@"C:\Users");
        await viewModel.UpCommand.Execute();
        viewModel.RemotePath.Should().Be(@"C:\");

        await viewModel.CreateDirectoryAsync("bad:name");
        viewModel.HasError.Should().BeTrue("':' is not allowed in a Windows file name");
        await viewModel.CreateDirectoryAsync("New");
        protocol.Files!.Created.Should().Equal(@"C:\New");

        // One window per session.
        SessionTabMenu.ShowUltraVncFileTransfer(tab, TestHost.MainWindow).Should().BeSameAs(dialog);

        dialog.Close();
        Pump();
        protocol.Files.Disposed.Should().BeTrue();
        SessionTabMenu.ShowUltraVncFileTransfer(tab, TestHost.MainWindow).Should().NotBeSameAs(dialog);
        foreach (var window in TestHost.MainWindow.OwnedWindows.OfType<SshFileTransferDialog>().ToList())
            window.Close();

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task UltraVncFileTransfer_WhenTheServerRefuses_ShowsTheError()
    {
        await ResetAsync();
        var protocol = new FakeVncProtocol { RefuseFileTransfer = true };
        var tab = await AddVncTabAsync(protocol);

        var dialog = SessionTabMenu.ShowUltraVncFileTransfer(tab, TestHost.MainWindow);
        var viewModel = (SshFileTransferViewModel)dialog.DataContext!;
        await viewModel.ConnectCommand.Execute();

        viewModel.IsConnected.Should().BeFalse();
        viewModel.HasError.Should().BeTrue();
        viewModel.Status.Should().Contain("does not allow");
        dialog.Close();

        await ResetAsync();
    }

    [AvaloniaFact]
    public async Task ChatAndFileTransfer_AreDisabledForOtherProtocols()
    {
        await ResetAsync();
        var tab = new SessionTabViewModel(new FakePlainProtocol(),
            new ConnectionParameters { Hostname = "raw", Port = 1, Protocol = ProtocolType.Raw });
        SessionDock.AddSession(tab);
        await tab.ConnectAsync();

        tab.TextChat.Should().BeNull();
        var menu = SessionTabMenu.Build(tab, TestHost.MainWindow);
        SessionTabMenu.Find(menu, SessionTabMenu.StartChat)!.IsEnabled.Should().BeFalse();
        SessionTabMenu.Find(menu, SessionTabMenu.TransferFileUltraVnc)!.IsEnabled.Should().BeFalse();

        await ResetAsync();
    }

    private static async Task<SessionTabViewModel> AddVncTabAsync(FakeVncProtocol protocol, IProtocolFactory? factory = null)
    {
        var tab = new SessionTabViewModel(protocol,
            new ConnectionParameters { Hostname = "vnc-host", Port = 5900, Protocol = ProtocolType.Vnc })
        {
            Factory = factory,
        };
        SessionDock.AddSession(tab);
        await tab.ConnectAsync();
        Pump();
        return tab;
    }

    private sealed class FakeVncFactory : IProtocolFactory
    {
        public List<FakeVncProtocol> Created { get; } = [];

        public IProtocol Create(ProtocolType type)
        {
            var protocol = new FakeVncProtocol();
            Created.Add(protocol);
            return protocol;
        }
    }

    private sealed class FakeVncProtocol : ProtocolBase, ITextChatProtocol, IFileTransferProtocol
    {
        public List<string> ChatCalls { get; } = [];

        public int FileSessionsOpened { get; private set; }

        public bool RefuseFileTransfer { get; init; }

        public FakeFileSession? Files { get; private set; }

        public event EventHandler<TextChatEvent>? TextChatReceived;

        public void Raise(TextChatEvent e) => TextChatReceived?.Invoke(this, e);

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

        public void OpenTextChat() => ChatCalls.Add("open");

        public bool SendTextChat(string text)
        {
            ChatCalls.Add("send:" + text);
            return State == ConnectionState.Connected;
        }

        public void CloseTextChat() => ChatCalls.Add("close");

        public Task<IRemoteFileSession> OpenFileTransferAsync(CancellationToken ct = default)
        {
            FileSessionsOpened++;
            if (RefuseFileTransfer)
                throw new UnauthorizedAccessException("The VNC server does not allow file transfer.");
            Files = new FakeFileSession();
            return Task.FromResult<IRemoteFileSession>(Files);
        }
    }

    private sealed class FakeFileSession : IRemoteFileSession
    {
        public List<string> Created { get; } = [];

        public bool Disposed { get; private set; }

        public bool IsConnected => !Disposed;

        public string HomeDirectory => @"C:\";

        public Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RemoteFileEntry>>(path == @"C:\"
                ?
                [
                    new RemoteFileEntry("Users", @"C:\Users", true, false, 0, DateTime.Now),
                    new RemoteFileEntry("boot.ini", @"C:\boot.ini", false, false, 10, DateTime.Now),
                ]
                : []);

        public Task UploadFileAsync(string localPath, string remotePath, IProgress<TransferProgress>? progress = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DownloadFileAsync(string remotePath, string localPath, IProgress<TransferProgress>? progress = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default)
        {
            Created.Add(remotePath);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(RemoteFileEntry entry, CancellationToken ct = default) => Task.CompletedTask;

        public string Combine(string directory, string name) => directory.TrimEnd('\\') + "\\" + name;

        public string Parent(string path)
        {
            var trimmed = path.TrimEnd('\\');
            var index = trimmed.LastIndexOf('\\');
            return index <= 2 ? @"C:\" : trimmed[..index];
        }

        public bool IsValidName(string name) => name.Length > 0 && name.IndexOfAny(['\\', '/', ':']) < 0;

        public void Dispose() => Disposed = true;
    }
}
