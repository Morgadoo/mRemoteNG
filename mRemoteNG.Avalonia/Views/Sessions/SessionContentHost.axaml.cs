using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Material.Icons;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Localization;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Avalonia.Views.Sessions;

/// <summary>
/// Hosts one session's view inside its panel: the connection frame colour, the state banner
/// (with Reconnect) and the protocol's control. The control is released while the session is
/// shown full screen, and replaced when a reconnect creates a new one.
/// </summary>
public partial class SessionContentHost : UserControl
{
    private readonly SessionTabViewModel? _session;

    public SessionContentHost() => InitializeComponent();

    public SessionContentHost(SessionTabViewModel session) : this()
    {
        _session = session;
        DataContext = session;
        session.PropertyChanged += OnSessionPropertyChanged;
        ReconnectButton.Click += OnReconnectClick;
        ReconnectButton.IsVisible = SessionsDockable.CanReconnect(session);
        Attach();
    }

    public SessionTabViewModel? Session => _session;

    /// <summary>The control currently hosted (null while detached or when the protocol has none).</summary>
    public Control? HostedView => ViewHost.Child;

    /// <summary>Gives the protocol's control back (before this host is discarded).</summary>
    public void Release()
    {
        if (_session is not null)
            _session.PropertyChanged -= OnSessionPropertyChanged;
        ViewHost.Child = null;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionTabViewModel.ContentView) or nameof(SessionTabViewModel.IsDetached)
            or nameof(SessionTabViewModel.State))
        {
            Attach();
        }
    }

    private void Attach()
    {
        if (_session is null) return;
        var view = _session.IsDetached ? null : _session.ContentView;
        if (!ReferenceEquals(ViewHost.Child, view))
        {
            // The control may come from another window (full screen, Move to Panel into a floating panel).
            if (view is not null)
                LayoutFlush.BeforeAttach(view, LayoutFlush.RootOf(this));
            ViewHost.Child = view;
            // A reconnect brings a new control: keep the keyboard in the visible session.
            if (view is not null && IsEffectivelyVisible)
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() => view.Focus());
        }

        Placeholder.Text = _session.IsDetached
            ? Localizer.Get("SessionShownFullScreen")
            : _session.ContentView is null
                ? _session.State == ConnectionState.Connected
                    ? Localizer.Get("SessionRunsInOwnWindow")
                    : string.Empty
                : string.Empty;
        PlaceholderIcon.Kind = _session.IsDetached ? MaterialIconKind.Fullscreen : MaterialIconKind.OpenInNew;
        PlaceholderPanel.IsVisible = Placeholder.Text.Length > 0;
    }

    private async void OnReconnectClick(object? sender, RoutedEventArgs e)
    {
        if (_session?.Panel?.Owner is { } dock && SessionsDockable.CanReconnect(_session))
            await dock.ReconnectSessionAsync(_session);
    }
}
