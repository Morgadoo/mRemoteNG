using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using mRemoteNG.Avalonia.ViewModels.Docking;

namespace mRemoteNG.Avalonia.Views.Sessions;

/// <summary>
/// Tab menu ▸ Full screen: shows one session's view in its own full-screen window and gives it
/// back to its panel when closed ("Exit full screen", or the session being closed).
/// </summary>
public partial class SessionFullScreenWindow : Window
{
    private static readonly Dictionary<SessionTabViewModel, SessionFullScreenWindow> Open = [];
    private readonly SessionTabViewModel? _session;

    public SessionFullScreenWindow() => InitializeComponent();

    private SessionFullScreenWindow(SessionTabViewModel session) : this()
    {
        _session = session;
        DataContext = session;
        ExitButton.Click += (_, _) => Close();
        ConnectionBar.PointerEntered += (_, _) => ConnectionBar.Opacity = 1;
        ConnectionBar.PointerExited += (_, _) => ConnectionBar.Opacity = 0.25;
        session.PropertyChanged += OnSessionPropertyChanged;
        Closing += (_, _) =>
        {
            // Give the control back before the panel re-adds it in another window.
            ViewHost.Child = null;
            LayoutFlush.Run(this);
        };
        Closed += OnClosed;
    }

    /// <summary>The window showing <paramref name="session"/> full screen, if any.</summary>
    public static SessionFullScreenWindow? For(SessionTabViewModel session) => Open.GetValueOrDefault(session);

    public static bool IsFullScreen(SessionTabViewModel session) => Open.ContainsKey(session);

    /// <summary>The control shown (for tests).</summary>
    public Control? HostedView => ViewHost.Child;

    /// <summary>Moves the session's view into a new full-screen window.</summary>
    public static SessionFullScreenWindow Enter(SessionTabViewModel session, Window? owner)
    {
        if (Open.TryGetValue(session, out var existing))
        {
            existing.Activate();
            return existing;
        }

        // The panel host releases the control first; a control can only have one parent.
        session.IsDetached = true;
        var window = new SessionFullScreenWindow(session);
        Open[session] = window;
        if (session.ContentView is { } view)
            LayoutFlush.BeforeAttach(view, window);
        window.ViewHost.Child = session.ContentView;
        if (owner is not null)
            window.Show(owner);
        else
            window.Show();
        session.ContentView?.Focus();
        return window;
    }

    /// <summary>Gives the session back to its panel.</summary>
    public static void Exit(SessionTabViewModel session)
    {
        if (Open.TryGetValue(session, out var window))
            window.Close();
    }

    public static void Toggle(SessionTabViewModel session, Window? owner)
    {
        if (IsFullScreen(session))
            Exit(session);
        else
            Enter(session, owner);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Ctrl+Alt+Enter leaves full screen (keys the remote side rarely needs).
        if (e.Key == Key.Enter && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt))
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_session is null) return;
        if (e.PropertyName == nameof(SessionTabViewModel.ContentView))
            ViewHost.Child = _session.ContentView;
        else if (e.PropertyName == nameof(SessionTabViewModel.Panel) && _session.Panel is null)
            Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_session is null) return;
        _session.PropertyChanged -= OnSessionPropertyChanged;
        ViewHost.Child = null;
        Open.Remove(_session);
        _session.IsDetached = false;
    }
}
