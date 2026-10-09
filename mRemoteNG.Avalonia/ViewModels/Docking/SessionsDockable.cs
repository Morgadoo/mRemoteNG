using System.Collections.ObjectModel;
using Avalonia.Controls;
using Dock.Model.Mvvm.Controls;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Core.Settings;
using mRemoteNG.Protocols.Abstractions;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

/// <summary>Dock panel for active remote connection sessions (tabbed).</summary>
public sealed class SessionsDockable : Document
{
    private SessionTabViewModel? _activeSession;
    private readonly LogPanelDockable? _log;
    private readonly AppSettingsService? _settings;
    private readonly CloseConfirmationService? _closeConfirmation;

    public SessionsDockable()
    {
        Id = "Sessions";
        Title = "Sessions";
    }

    public SessionsDockable(
        LogPanelDockable log,
        AppSettingsService? settings = null,
        CloseConfirmationService? closeConfirmation = null) : this()
    {
        _log = log;
        _settings = settings;
        _closeConfirmation = closeConfirmation;
    }

    public ObservableCollection<SessionTabViewModel> Sessions { get; } = [];

    public SessionTabViewModel? ActiveSession
    {
        get => _activeSession;
        set
        {
            if (ReferenceEquals(_activeSession, value)) return;
            _activeSession = value;
            OnPropertyChanged(nameof(ActiveSession));
        }
    }

    public void AddSession(SessionTabViewModel session)
    {
        session.CloseRequested += OnCloseRequested;
        Sessions.Add(session);
        ActiveSession = session;
    }

    /// <summary>Disconnects, removes and disposes a session tab.</summary>
    public async Task CloseSessionAsync(SessionTabViewModel session)
    {
        session.CloseRequested -= OnCloseRequested;
        var index = Sessions.IndexOf(session);
        if (index < 0) return;

        Sessions.RemoveAt(index);
        if (ReferenceEquals(ActiveSession, session))
            ActiveSession = Sessions.Count == 0 ? null : Sessions[Math.Min(index, Sessions.Count - 1)];

        await session.DisconnectAsync();
        session.Dispose();
    }

    /// <summary>Writes a connection error to the log panel.</summary>
    public void ReportError(string message) => _log?.Log(message, LogLevel.Error);

    private async void OnCloseRequested(object? sender, EventArgs e)
    {
        if (sender is not SessionTabViewModel session) return;

        // Only asks when "Confirm closing connections" is set to every connection.
        if (_closeConfirmation is not null && !await _closeConfirmation.ConfirmCloseConnectionAsync(session.Title))
            return;
        await CloseSessionAsync(session);
    }

    /// <summary>
    /// Creates and adds a new session from connection parameters.
    /// The protocol implementation is resolved from <paramref name="factory"/>.
    /// </summary>
    public async Task OpenConnectionAsync(
        ConnectionParameters parameters,
        IProtocolFactory factory,
        CancellationToken ct = default)
    {
        // Global defaults (port, username, SSH key, timeout, keep-alive) for anything left unset.
        if (_settings is not null)
            parameters = _settings.Current.WithDefaults(parameters);

        var protocol = factory.Create(parameters.Protocol);
        var tab = new SessionTabViewModel(protocol, parameters);
        AddSession(tab);

        try
        {
            await tab.ConnectAsync(ct);
        }
        catch (Exception ex)
        {
            tab.SetError(ex.Message);
            _log?.Log($"Connection to {parameters.Hostname}:{parameters.Port} failed: {ex.Message}", LogLevel.Error);
        }
    }
}

/// <summary>Represents a single active connection tab.</summary>
public sealed class SessionTabViewModel : ReactiveObject, IDisposable
{
    private readonly IProtocol _protocol;
    private string _title;
    private bool _isConnected;
    private string _statusText = string.Empty;
    private bool _disposed;

    public string Id { get; } = Guid.NewGuid().ToString();
    public string ProtocolName { get; }
    public string Hostname { get; }
    public ConnectionParameters Parameters { get; }

    /// <summary>
    /// The Avalonia control produced by the protocol's IVisualProtocol.CreateView(),
    /// or null if the protocol does not provide a visual (e.g. external app).
    /// </summary>
    public Control? ContentView { get; }

    public string Title
    {
        get => _title;
        private set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set => this.RaiseAndSetIfChanged(ref _isConnected, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    public ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> CloseCommand { get; }

    /// <summary>Raised when the user closes this tab; the owning dock removes and disposes it.</summary>
    public event EventHandler? CloseRequested;

    public SessionTabViewModel(IProtocol protocol, ConnectionParameters parameters)
    {
        _protocol = protocol;
        Parameters = parameters;
        Hostname = parameters.Hostname;
        ProtocolName = parameters.Protocol.ToString().ToUpperInvariant();
        _title = $"{ProtocolName}: {parameters.Hostname}";

        // Wire protocol events
        _protocol.StateChanged += OnStateChanged;
        _protocol.StatusMessage += OnStatusMessage;

        // Create visual surface if supported
        if (protocol is IVisualProtocol visual)
            ContentView = visual.CreateView();

        CloseCommand = ReactiveUI.ReactiveCommand.Create(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _protocol.ConnectAsync(Parameters, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        try { await _protocol.DisconnectAsync(ct); }
        catch { /* best-effort disconnect */ }
    }

    /// <summary>Show an error state on this tab.</summary>
    public void SetError(string message)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsConnected = false;
            Title = $"\u26a0 {ProtocolName}: {Hostname}";
            StatusText = message;
        });
    }

    private void OnStateChanged(object? sender, ConnectionState state)
    {
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsConnected = state == ConnectionState.Connected;
            Title = state switch
            {
                ConnectionState.Connecting => $"\u23f3 {ProtocolName}: {Hostname}",
                ConnectionState.Connected => $"{ProtocolName}: {Hostname}",
                ConnectionState.Reconnecting => $"\u21ba {ProtocolName}: {Hostname}",
                ConnectionState.Error => $"\u26a0 {ProtocolName}: {Hostname}",
                _ => $"\u2716 {ProtocolName}: {Hostname}",
            };
        });
    }

    private void OnStatusMessage(object? sender, string message) =>
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText = message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _protocol.StateChanged -= OnStateChanged;
        _protocol.StatusMessage -= OnStatusMessage;
        _protocol.Dispose();
    }
}
