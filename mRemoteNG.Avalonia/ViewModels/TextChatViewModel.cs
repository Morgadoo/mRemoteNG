using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Threading;
using mRemoteNG.Core.Localization;
using mRemoteNG.Protocols.Abstractions;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>A line in the chat: a message from either side, or a notice (chat opened, closed, not sent).</summary>
public sealed record TextChatLine(string Sender, string Text, string Time, bool IsLocal, bool IsNotice)
{
    public bool IsRemote => !IsLocal && !IsNotice;
}

/// <summary>
/// The text chat of one session (UltraVNC chat). It outlives the chat window, so messages that arrive while the
/// window is closed are kept, and follows the session's protocol across reconnects.
/// </summary>
public sealed class TextChatViewModel : ReactiveObject, IDisposable
{
    private readonly Func<string> _peerName;
    private ITextChatProtocol? _protocol;
    private string _draft = string.Empty;
    private bool _isWindowOpen;
    private bool _chatOpen;

    public TextChatViewModel(Func<string> peerName, ITextChatProtocol? protocol)
    {
        _peerName = peerName;
        Attach(protocol);
        SendCommand = ReactiveCommand.Create(Send,
            this.WhenAnyValue(x => x.Draft, draft => !string.IsNullOrWhiteSpace(draft)));
    }

    /// <summary>Who is on the other end (the connection's name).</summary>
    public string PeerName => _peerName();

    public string Title => Localizer.Format("TextChatTitleFormat", PeerName);

    public ObservableCollection<TextChatLine> Lines { get; } = [];

    public string Draft
    {
        get => _draft;
        set => this.RaiseAndSetIfChanged(ref _draft, value);
    }

    public bool IsWindowOpen => _isWindowOpen;

    public ReactiveCommand<Unit, Unit> SendCommand { get; }

    /// <summary>Raised on the UI thread when the remote side starts chatting while no chat window is open.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>Raised when the session is closed; the chat window closes.</summary>
    public event EventHandler? Ended;

    /// <summary>Follows the session's current protocol (after a reconnect); null detaches.</summary>
    public void Attach(ITextChatProtocol? protocol)
    {
        if (ReferenceEquals(_protocol, protocol)) return;
        if (_protocol is not null)
            _protocol.TextChatReceived -= OnTextChatReceived;
        _protocol = protocol;
        _chatOpen = false;
        if (_protocol is not null)
            _protocol.TextChatReceived += OnTextChatReceived;
    }

    /// <summary>The chat window opened: opens the chat on the server unless the server opened it.</summary>
    public void WindowOpened()
    {
        _isWindowOpen = true;
        if (!_chatOpen)
            _protocol?.OpenTextChat();
        _chatOpen = true;
    }

    /// <summary>The chat window closed: closes the chat on the server.</summary>
    public void WindowClosed()
    {
        _isWindowOpen = false;
        if (_chatOpen)
            _protocol?.CloseTextChat();
        _chatOpen = false;
    }

    private void Send()
    {
        var text = Draft.TrimEnd();
        if (text.Length == 0) return;
        if (_protocol?.SendTextChat(text) == true)
        {
            Add(Localizer.Get("TextChatYou"), text, isLocal: true);
            Draft = string.Empty;
        }
        else
        {
            AddNotice(Localizer.Get("TextChatNotSent"));
        }
    }

    private void OnTextChatReceived(object? sender, TextChatEvent e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            Receive(e);
        else
            Dispatcher.UIThread.Post(() => Receive(e));
    }

    internal void Receive(TextChatEvent e)
    {
        switch (e.Kind)
        {
            case TextChatEventKind.Message:
                _chatOpen = true;
                Add(PeerName, e.Text, isLocal: false);
                break;
            case TextChatEventKind.Opened:
                _chatOpen = true;
                AddNotice(Localizer.Format("TextChatOpenedFormat", PeerName));
                break;
            case TextChatEventKind.Closed:
                if (!_chatOpen) return; // UltraVNC sends "closed" and "finished"; one notice is enough
                _chatOpen = false;
                AddNotice(Localizer.Format("TextChatClosedFormat", PeerName));
                return;
        }
        if (!_isWindowOpen)
            OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Add(string sender, string text, bool isLocal) =>
        Lines.Add(new TextChatLine(sender, text, Now(), isLocal, IsNotice: false));

    private void AddNotice(string text) =>
        Lines.Add(new TextChatLine(string.Empty, text, Now(), IsLocal: false, IsNotice: true));

    private static string Now() => DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);

    public void Dispose()
    {
        Attach(null);
        Ended?.Invoke(this, EventArgs.Empty);
    }
}
