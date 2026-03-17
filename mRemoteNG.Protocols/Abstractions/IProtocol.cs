using System.ComponentModel;

namespace mRemoteNG.Protocols.Abstractions;

/// <summary>
/// Connection state for a protocol session.
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Error
}

/// <summary>
/// Describes the protocol type for factory resolution.
/// </summary>
public enum ProtocolType
{
    Ssh,
    SshSftp,
    Telnet,
    Rlogin,
    Rdp,
    Vnc,
    Http,
    Https,
    PowerShell,
    Serial,
    ExternalApp
}

/// <summary>
/// Parameters that describe a remote connection.
/// All protocols receive this via <see cref="IProtocol.ConnectAsync"/>.
/// </summary>
public sealed class ConnectionParameters
{
    public required string Hostname { get; init; }
    public required int Port { get; init; }
    public required ProtocolType Protocol { get; init; }

    // Credentials
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Domain { get; init; }
    public string? PrivateKeyPath { get; init; }
    public string? PrivateKeyPassphrase { get; init; }

    // Display
    public string DisplayName => $"{Hostname}:{Port}";

    // Protocol-specific extras (string → string bag for extensibility)
    public IReadOnlyDictionary<string, string> Extras { get; init; }
        = new Dictionary<string, string>();
}

/// <summary>
/// Core contract every protocol implementation must satisfy.
/// Implementations are created by <see cref="IProtocolFactory"/>.
/// </summary>
public interface IProtocol : IDisposable
{
    /// <summary>Current connection state.</summary>
    ConnectionState State { get; }

    /// <summary>
    /// Raised whenever <see cref="State"/> changes.
    /// Implementations must raise this on the UI thread.
    /// </summary>
    event EventHandler<ConnectionState> StateChanged;

    /// <summary>
    /// Raised when the remote side sends data or a status message.
    /// Used for logging / status bar.
    /// </summary>
    event EventHandler<string> StatusMessage;

    /// <summary>Opens the connection.</summary>
    Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default);

    /// <summary>Gracefully closes the connection.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends a protocol-specific keepalive (if supported).
    /// Default implementations may be no-op.
    /// </summary>
    Task SendKeepAliveAsync(CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// Protocol that exposes an Avalonia visual surface
/// (terminal, canvas, native embed, webview…).
/// The returned Control is placed inside the session tab.
/// </summary>
public interface IVisualProtocol : IProtocol
{
    /// <summary>Returns the Avalonia control to embed in the session tab.</summary>
    Avalonia.Controls.Control CreateView();
}

/// <summary>Creates IProtocol instances for a given <see cref="ProtocolType"/>.</summary>
public interface IProtocolFactory
{
    IProtocol Create(ProtocolType type);
}

/// <summary>
/// Base class that provides boilerplate for state management and event dispatch.
/// </summary>
public abstract class ProtocolBase : IProtocol
{
    private ConnectionState _state = ConnectionState.Disconnected;
    private bool _disposed;

    public ConnectionState State
    {
        get => _state;
        protected set
        {
            if (_state == value) return;
            _state = value;
            RaiseStateChanged(value);
        }
    }

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<string>? StatusMessage;

    public abstract Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default);
    public abstract Task DisconnectAsync(CancellationToken ct = default);

    protected void RaiseStateChanged(ConnectionState state) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => StateChanged?.Invoke(this, state));

    protected void RaiseStatus(string message) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => StatusMessage?.Invoke(this, message));

    protected virtual void Dispose(bool disposing) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
