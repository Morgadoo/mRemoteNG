using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.ViewModels.Docking;

namespace mRemoteNG.Avalonia.Views.Sessions;

/// <summary>
/// The text chat window of a session (UltraVNC chat). One window per session: <see cref="ShowFor"/> brings an open
/// one to the front. Opening the window opens the chat on the server and closing it closes the chat there.
/// </summary>
public partial class TextChatWindow : Window
{
    private static readonly Dictionary<TextChatViewModel, TextChatWindow> OpenWindows = [];

    public TextChatWindow() => InitializeComponent();

    public TextChatWindow(TextChatViewModel chat) : this()
    {
        DataContext = chat;
        chat.Lines.CollectionChanged += OnLinesChanged;
        chat.Ended += OnChatEnded;
        Opened += (_, _) =>
        {
            chat.WindowOpened();
            DraftBox.Focus();
        };
        Closed += (_, _) =>
        {
            chat.Lines.CollectionChanged -= OnLinesChanged;
            chat.Ended -= OnChatEnded;
            OpenWindows.Remove(chat);
            chat.WindowClosed();
        };
    }

    /// <summary>The open chat window of <paramref name="chat"/>, if any (for tests).</summary>
    public static TextChatWindow? Find(TextChatViewModel chat) => OpenWindows.GetValueOrDefault(chat);

    /// <summary>Shows the session's chat window (or activates it); null when the session has no text chat.</summary>
    public static TextChatWindow? ShowFor(SessionTabViewModel session, Window? owner)
    {
        if (session.TextChat is not { } chat)
            return null;
        if (OpenWindows.TryGetValue(chat, out var existing))
        {
            existing.Activate();
            return existing;
        }
        var window = new TextChatWindow(chat);
        OpenWindows[chat] = window;
        if (owner is not null)
            window.Show(owner);
        else
            window.Show();
        return window;
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => LinesScroller.ScrollToEnd(), DispatcherPriority.Background);

    private void OnChatEnded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Close);
}
