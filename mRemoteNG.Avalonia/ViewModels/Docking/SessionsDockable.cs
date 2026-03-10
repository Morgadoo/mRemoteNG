using System.Collections.ObjectModel;
using Avalonia.Controls;
using Dock.Model.Mvvm.Controls;
using mRemoteNG.Protocols.Abstractions;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

/// <summary>Dock panel for active remote connection sessions (tabbed).</summary>
public sealed class SessionsDockable : Document
{
    private SessionTabViewModel? _activeSession;

    public SessionsDockable()
    {
        Id = "Sessions";
        Title = "Sessions";
    }

    public ObservableCollection<SessionTabViewModel> Sessions { get; } = [];

    public SessionTabViewModel? ActiveSession
    {
        get => _activeSession;
        set => this.RaiseAndSetIfChanged(ref _activeSession, value);
    }

    public void AddSession(SessionTabViewModel session)
    {
        Sessions.Add(session);
        ActiveSession = session;
    }

    public void CloseSession(SessionTabViewModel session)
    {
        _ = session.DisconnectAsync();
        Sessions.Remove(session);
        ActiveSession = Sessions.LastOrDefault();
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
        var protocol = factory.Create(parameters.Protocol);
        var tab = new SessionTabViewModel(protocol, parameters);
        AddSession(tab);
        await tab.ConnectAsync(ct);
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

        CloseCommand = ReactiveUI.ReactiveCommand.CreateFromTask(async () =>
        {
            await DisconnectAsync();
            // Parent ViewModel will remove this tab from Sessions collection
        });
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _protocol.ConnectAsync(Parameters, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _protocol.DisconnectAsync(ct);
    }

    private void OnStateChanged(object? sender, ConnectionState state)
    {
        IsConnected = state == ConnectionState.Connected;
        Title = state switch
        {
            ConnectionState.Connecting => $"⏳ {ProtocolName}: {Hostname}",
            ConnectionState.Connected => $"{ProtocolName}: {Hostname}",
            ConnectionState.Reconnecting => $"↺ {ProtocolName}: {Hostname}",
            ConnectionState.Error => $"⚠ {ProtocolName}: {Hostname}",
            _ => $"✖ {ProtocolName}: {Hostname}",
        };
    }

    private void OnStatusMessage(object? sender, string message) =>
        StatusText = message;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _protocol.StateChanged -= OnStateChanged;
        _protocol.StatusMessage -= OnStatusMessage;
        _protocol.Dispose();
    }
}
