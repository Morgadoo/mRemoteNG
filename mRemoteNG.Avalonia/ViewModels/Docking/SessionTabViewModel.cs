using System.ComponentModel;
using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;
using mRemoteNG.Protocols.Abstractions;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

/// <summary>
/// One session tab. The protocol instance can be replaced (reconnect), so <see cref="Protocol"/>,
/// <see cref="ContentView"/> and <see cref="Parameters"/> raise change notifications.
/// </summary>
public sealed class SessionTabViewModel : ReactiveObject, IDisposable
{
    private IProtocol _protocol;
    private ConnectionInfo? _connection;
    private ConnectionParameters _parameters;
    private Control? _contentView;
    private ConnectionState _state = ConnectionState.Disconnected;
    private AppSettings? _titleSettings;
    private string _baseTitle = string.Empty;
    private string? _customTitle;
    private string _title = string.Empty;
    private string _statusText = string.Empty;
    private string? _reconnectStatus;
    private bool _hasStarted;
    private bool _isDetached;
    private bool _isMultiSshTarget = true;
    private SessionPanelViewModel? _panel;
    private bool _disposed;

    public SessionTabViewModel(IProtocol protocol, ConnectionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(parameters);
        _protocol = protocol;
        _parameters = parameters;
        SourceParameters = parameters;

        _protocol.StateChanged += OnStateChanged;
        _protocol.StatusMessage += OnStatusMessage;
        _state = protocol.State;
        if (protocol is IVisualProtocol visual)
            _contentView = visual.CreateView();

        CloseCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        UpdateTitle();
    }

    public string Id { get; } = Guid.NewGuid().ToString();

    /// <summary>Upper-case protocol name of the running session ("SSH", "RDP", "RAW").</summary>
    public string ProtocolName => _parameters.Protocol.ToString().ToUpperInvariant();

    public string Hostname => _parameters.Hostname;

    /// <summary>
    /// The host the user connected to: the connection's hostname when there is one (through an SSH
    /// tunnel <see cref="Hostname"/> is the local end, 127.0.0.1), else the session's.
    /// </summary>
    public string DisplayHostname => _connection is { Hostname.Length: > 0 } ? _connection.Hostname : _parameters.Hostname;

    /// <summary>The port that goes with <see cref="DisplayHostname"/>.</summary>
    public int DisplayPort => _connection is { Hostname.Length: > 0, Port: > 0 } ? _connection.Port : _parameters.Port;

    /// <summary>The parameters the current protocol instance connected with (after preparation and global defaults).</summary>
    public ConnectionParameters Parameters => _parameters;

    /// <summary>The parameters the session was opened with, before global defaults (used to reopen ad-hoc sessions).</summary>
    public ConnectionParameters SourceParameters { get; init; }

    /// <summary>The connection-tree node this session was opened from (null for ad-hoc sessions).</summary>
    public ConnectionInfo? Connection
    {
        get => _connection;
        init
        {
            _connection = value;
            if (_connection is not null)
                _connection.PropertyChanged += OnConnectionPropertyChanged;
            UpdateTitle();
        }
    }

    /// <summary>The options the session was opened with.</summary>
    public ConnectOptions Options { get; init; } = ConnectOptions.Default;

    /// <summary>Resources from connection preparation (tunnels, post-connect actions); released on close.</summary>
    public PreparedConnection? Prepared { get; internal set; }

    /// <summary>Creates protocol instances for reconnects; null when the session cannot be reopened.</summary>
    public IProtocolFactory? Factory { get; init; }

    /// <summary>The protocol instance, for capability checks (ITerminalProtocol, ISpecialKeysProtocol…).</summary>
    public IProtocol Protocol => _protocol;

    /// <summary>The control from IVisualProtocol.CreateView(), or null when the protocol has no view (external app).</summary>
    public Control? ContentView
    {
        get => _contentView;
        private set => this.RaiseAndSetIfChanged(ref _contentView, value);
    }

    /// <summary>The panel (tab group) the session lives in.</summary>
    public SessionPanelViewModel? Panel
    {
        get => _panel;
        internal set => this.RaiseAndSetIfChanged(ref _panel, value);
    }

    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(IsConnected));
            this.RaisePropertyChanged(nameof(ShowStatusBanner));
            this.RaisePropertyChanged(nameof(StatusBannerText));
            UpdateTitle();
        }
    }

    public bool IsConnected => _state == ConnectionState.Connected;

    /// <summary>True once the current protocol instance reached <see cref="ConnectionState.Connected"/>.</summary>
    public bool HasBeenConnected { get; private set; }

    /// <summary>Set while the app itself disconnects the session (close, reconnect, disconnect all) so auto-reconnect stays out.</summary>
    internal bool SuppressAutoReconnect { get; set; }

    /// <summary>The generated title (connection name, protocol and logon per the Tabs &amp; Panels options).</summary>
    public string BaseTitle
    {
        get => _baseTitle;
        private set => this.RaiseAndSetIfChanged(ref _baseTitle, value);
    }

    /// <summary>Title set with "Rename tab"; null to use <see cref="BaseTitle"/>.</summary>
    public string? CustomTitle
    {
        get => _customTitle;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            this.RaiseAndSetIfChanged(ref _customTitle, normalized);
            UpdateTitle();
        }
    }

    /// <summary>The name shown on the tab, without the state marker.</summary>
    public string DisplayTitle => _customTitle ?? _baseTitle;

    /// <summary>Tab text including a state marker (⏳ connecting, ↺ reconnecting, ⚠ error, ✖ disconnected).</summary>
    public string Title
    {
        get => _title;
        private set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            this.RaiseAndSetIfChanged(ref _statusText, value);
            this.RaisePropertyChanged(nameof(StatusBannerText));
            this.RaisePropertyChanged(nameof(ToolTipText));
        }
    }

    /// <summary>Auto-reconnect progress ("Reconnecting in 4 s (attempt 2 of 5)"); null when idle.</summary>
    public string? ReconnectStatus
    {
        get => _reconnectStatus;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _reconnectStatus, value);
            this.RaisePropertyChanged(nameof(IsReconnecting));
            this.RaisePropertyChanged(nameof(ShowStatusBanner));
            this.RaisePropertyChanged(nameof(StatusBannerText));
            this.RaisePropertyChanged(nameof(ToolTipText));
            UpdateTitle();
        }
    }

    public bool IsReconnecting => _reconnectStatus is not null;

    /// <summary>Show the strip above the session view (not connected, or reconnecting).</summary>
    public bool ShowStatusBanner => _hasStarted && (_state is ConnectionState.Error or ConnectionState.Disconnected or ConnectionState.Reconnecting
                                                    || _reconnectStatus is not null);

    public string StatusBannerText
    {
        get
        {
            if (_reconnectStatus is not null)
                return _reconnectStatus;
            var state = _state switch
            {
                ConnectionState.Error => "Connection error",
                ConnectionState.Reconnecting => "Reconnecting…",
                ConnectionState.Connecting => "Connecting…",
                _ => "Disconnected",
            };
            // The last status message explains errors; after a plain disconnect it is stale ("Connected to …").
            return _state != ConnectionState.Error || string.IsNullOrWhiteSpace(_statusText) ? state : $"{state}: {_statusText}";
        }
    }

    public string ToolTipText
    {
        get
        {
            var text = $"{DisplayTitle}\n{SessionTabAppearance.ProtocolDisplayName(_connection, _parameters)} {DisplayHostname}:{DisplayPort}";
            if (_connection is { Panel.Length: > 0 } || _panel is not null)
                text += $"\nPanel: {_panel?.Name ?? _connection?.Panel}";
            if (_reconnectStatus is not null)
                text += $"\n{_reconnectStatus}";
            else if (!string.IsNullOrWhiteSpace(_statusText))
                text += $"\n{_statusText}";
            return text;
        }
    }

    /// <summary>True while the session view is shown in its own full-screen window.</summary>
    public bool IsDetached
    {
        get => _isDetached;
        set => this.RaiseAndSetIfChanged(ref _isDetached, value);
    }

    /// <summary>Multi-SSH sends to this session (when it is a terminal session).</summary>
    public bool IsMultiSshTarget
    {
        get => _isMultiSshTarget;
        set => this.RaiseAndSetIfChanged(ref _isMultiSshTarget, value);
    }

    public bool IsTerminal => _protocol is ITerminalProtocol;

    // ── Appearance ────────────────────────────────────────────────────────

    /// <summary>The connection's TabColor as a brush; null when none is set.</summary>
    public IBrush? TabColorBrush =>
        SessionTabAppearance.ParseTabColor(_connection?.TabColor) is { } color ? new SolidColorBrush(color) : null;

    public bool HasTabColor => TabColorBrush is not null;

    /// <summary>Tab background tint from TabColor (legacy painted the selected tab in that colour).</summary>
    public IBrush? TabTintBrush =>
        SessionTabAppearance.ParseTabColor(_connection?.TabColor) is { } color
            ? new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B))
            : null;

    /// <summary>The ConnectionFrameColor border around the session view; transparent when none.</summary>
    public IBrush FrameBrush =>
        _connection is not null && SessionTabAppearance.FrameColor(_connection.ConnectionFrameColor) is { } color
            ? new SolidColorBrush(color)
            : Brushes.Transparent;

    public bool HasFrame => _connection is not null && SessionTabAppearance.FrameColor(_connection.ConnectionFrameColor) is not null;

    public Thickness FrameThickness => HasFrame ? new Thickness(SessionTabAppearance.FrameWidth) : new Thickness(0);

    /// <summary>Gap between the frame and the session so the frame is never covered (legacy: 2 px).</summary>
    public Thickness FramePadding => HasFrame ? new Thickness(2) : new Thickness(0);

    public IReadOnlyList<EnvironmentTagBadge> EnvironmentTags =>
        SessionTabAppearance.SplitTags(_connection?.EnvironmentTags)
            .Select(t => new EnvironmentTagBadge(t, new SolidColorBrush(SessionTabAppearance.TagColor(t))))
            .ToList();

    public bool HasEnvironmentTags => EnvironmentTags.Count > 0;

    /// <summary>The connection's icon when it has one, else the protocol icon.</summary>
    public Bitmap? Icon
    {
        get
        {
            if (_connection is { Icon.Length: > 0 } && IconService.LoadIcon($"{_connection.Icon}.ico") is { } custom)
                return custom;
            return IconService.GetProtocolIcon(SessionTabAppearance.ProtocolIconKey(_parameters.Protocol));
        }
    }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    /// <summary>Raised when the user closes this tab; the owning dock removes and disposes it.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised (on the UI thread) when the current protocol's state changes.</summary>
    public event EventHandler<ConnectionState>? ProtocolStateChanged;

    /// <summary>Applies the Tabs &amp; Panels title options (called by the dock when they change).</summary>
    internal void ApplyTitleSettings(AppSettings? settings)
    {
        _titleSettings = settings;
        UpdateTitle();
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _hasStarted = true;
        this.RaisePropertyChanged(nameof(ShowStatusBanner));
        await _protocol.ConnectAsync(_parameters, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        try { await _protocol.DisconnectAsync(ct); }
        catch { /* best-effort disconnect */ }
    }

    /// <summary>Show an error state on this tab.</summary>
    public void SetError(string message)
    {
        RunOnUi(() =>
        {
            _hasStarted = true;
            StatusText = message;
            State = ConnectionState.Error;
            this.RaisePropertyChanged(nameof(ShowStatusBanner));
            this.RaisePropertyChanged(nameof(StatusBannerText));
        });
    }

    /// <summary>
    /// Swaps in a new protocol instance (reconnect). Returns the old one, which the caller disconnects
    /// and disposes; its view is removed from the visual tree before this returns.
    /// </summary>
    internal IProtocol ReplaceProtocol(IProtocol protocol, ConnectionParameters parameters)
    {
        var old = _protocol;
        old.StateChanged -= OnStateChanged;
        old.StatusMessage -= OnStatusMessage;

        _protocol = protocol;
        _parameters = parameters;
        HasBeenConnected = false;
        _protocol.StateChanged += OnStateChanged;
        _protocol.StatusMessage += OnStatusMessage;

        this.RaisePropertyChanged(nameof(Protocol));
        this.RaisePropertyChanged(nameof(Parameters));
        this.RaisePropertyChanged(nameof(Hostname));
        this.RaisePropertyChanged(nameof(ProtocolName));
        this.RaisePropertyChanged(nameof(IsTerminal));
        ContentView = protocol is IVisualProtocol visual ? visual.CreateView() : null;
        StatusText = string.Empty;
        State = protocol.State;
        UpdateTitle();
        return old;
    }

    private void OnStateChanged(object? sender, ConnectionState state) =>
        RunOnUi(() =>
        {
            if (!ReferenceEquals(sender, _protocol)) return; // a replaced protocol
            if (state == ConnectionState.Connected)
                HasBeenConnected = true;
            State = state;
            ProtocolStateChanged?.Invoke(this, state);
        });

    private void OnStatusMessage(object? sender, string message) =>
        RunOnUi(() =>
        {
            if (ReferenceEquals(sender, _protocol))
                StatusText = message;
        });

    private void OnConnectionPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        RunOnUi(() =>
        {
            UpdateTitle();
            this.RaisePropertyChanged(nameof(TabColorBrush));
            this.RaisePropertyChanged(nameof(HasTabColor));
            this.RaisePropertyChanged(nameof(TabTintBrush));
            this.RaisePropertyChanged(nameof(FrameBrush));
            this.RaisePropertyChanged(nameof(HasFrame));
            this.RaisePropertyChanged(nameof(FrameThickness));
            this.RaisePropertyChanged(nameof(FramePadding));
            this.RaisePropertyChanged(nameof(EnvironmentTags));
            this.RaisePropertyChanged(nameof(HasEnvironmentTags));
            this.RaisePropertyChanged(nameof(Icon));
        });

    private void UpdateTitle()
    {
        // Called from the constructor before every field is set.
        if (_parameters is null) return;
        BaseTitle = SessionTabAppearance.FormatTitle(_connection, _parameters, _titleSettings);
        var marker = _reconnectStatus is not null
            ? "↺ "
            : _state switch
            {
                ConnectionState.Connecting => "⏳ ",
                ConnectionState.Reconnecting => "↺ ",
                ConnectionState.Error => "⚠ ",
                ConnectionState.Disconnected when _hasStarted => "✖ ",
                _ => string.Empty,
            };
        Title = marker + DisplayTitle;
        this.RaisePropertyChanged(nameof(DisplayTitle));
        this.RaisePropertyChanged(nameof(ToolTipText));
    }

    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _protocol.StateChanged -= OnStateChanged;
        _protocol.StatusMessage -= OnStatusMessage;
        if (_connection is not null)
            _connection.PropertyChanged -= OnConnectionPropertyChanged;
        _protocol.Dispose();
    }
}
