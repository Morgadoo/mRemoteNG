using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Core.Connection;

namespace mRemoteNG.Protocols.Abstractions;

/// <summary>Choices made for a single connection attempt ("Connect with options").</summary>
public sealed record ConnectOptions
{
    public static ConnectOptions Default { get; } = new();

    /// <summary>Connect without the stored username, password and domain.</summary>
    public bool NoCredentials { get; init; }

    /// <summary>Overrides the RDP console-session setting when not null.</summary>
    public bool? ConsoleSession { get; init; }

    /// <summary>Open the session full screen.</summary>
    public bool Fullscreen { get; init; }

    /// <summary>Open the session view-only (where the protocol supports it).</summary>
    public bool ViewOnly { get; init; }

    /// <summary>Panel to open the session in; null uses the connection's Panel property.</summary>
    public string? Panel { get; init; }
}

/// <summary>State shared by the preparation steps of one connection attempt.</summary>
public sealed class PreparationContext(ConnectionInfo connection, ConnectOptions options)
{
    public ConnectionInfo Connection { get; } = connection;

    public ConnectOptions Options { get; } = options;

    /// <summary>
    /// Resources that live as long as the session (tunnels, post-connect actions…). They are disposed
    /// in reverse order when the session closes, or immediately if preparation fails.
    /// </summary>
    public IList<IAsyncDisposable> Resources { get; } = new List<IAsyncDisposable>();
}

/// <summary>
/// A step that runs before a session connects: resolve credentials from an external provider,
/// resolve the address, open an SSH tunnel, run a pre-connect application, …
/// </summary>
public interface IConnectionPreparationStep
{
    /// <summary>Steps run in ascending order. Suggested ranges: 100 address, 200 credentials,
    /// 300 tunnels, 400 pre-connect actions.</summary>
    int Order { get; }

    /// <summary>Returns the (possibly rewritten) parameters for the next step.</summary>
    Task<ConnectionParameters> PrepareAsync(PreparationContext context, ConnectionParameters parameters, CancellationToken ct);
}

/// <summary>Parameters ready to connect with, plus the resources to release when the session ends.</summary>
public sealed class PreparedConnection(ConnectionParameters parameters, IReadOnlyList<IAsyncDisposable> resources, ILogger? logger = null)
    : IAsyncDisposable
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private int _disposed;

    public ConnectionParameters Parameters { get; } = parameters;

    public IReadOnlyList<IAsyncDisposable> Resources { get; } = resources;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await DisposeAllAsync(Resources, _logger);
    }

    internal static async Task DisposeAllAsync(IEnumerable<IAsyncDisposable> resources, ILogger logger)
    {
        foreach (var resource in resources.Reverse())
        {
            try
            {
                await resource.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Releasing a session resource failed");
            }
        }
    }
}

/// <summary>
/// Turns a connection-tree node into connection parameters: maps the node, applies
/// <see cref="ConnectOptions"/>, then runs every registered <see cref="IConnectionPreparationStep"/>.
/// </summary>
public sealed class ConnectionPreparer(IEnumerable<IConnectionPreparationStep> steps, ILogger<ConnectionPreparer>? logger = null)
{
    private readonly IReadOnlyList<IConnectionPreparationStep> _steps = steps.OrderBy(s => s.Order).ToList();
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public async Task<PreparedConnection> PrepareAsync(ConnectionInfo connection, ConnectOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        options ??= ConnectOptions.Default;

        var parameters = ApplyOptions(ConnectionParametersFactory.FromConnectionInfo(connection), options);
        var context = new PreparationContext(connection, options);
        try
        {
            foreach (var step in _steps)
                parameters = await step.PrepareAsync(context, parameters, ct);
        }
        catch
        {
            await PreparedConnection.DisposeAllAsync(context.Resources, _logger);
            throw;
        }

        return new PreparedConnection(parameters, context.Resources.ToList(), _logger);
    }

    public static ConnectionParameters ApplyOptions(ConnectionParameters parameters, ConnectOptions options)
    {
        if (options.NoCredentials)
            parameters = parameters with { Username = null, Password = null, Domain = null };

        if (options.ConsoleSession is { } console || options.ViewOnly)
        {
            var extras = new Dictionary<string, string>(parameters.Extras);
            if (options.ConsoleSession is { } consoleSession)
                extras[ConnectionParametersFactory.Keys.RdpConsole] = consoleSession ? "true" : "false";
            if (options.ViewOnly)
                extras[ConnectionParametersFactory.Keys.VncViewOnly] = "true";
            parameters = parameters with { Extras = extras };
        }
        return parameters;
    }
}

/// <summary>A session whose user input can be sent programmatically (e.g. Multi-SSH).</summary>
public interface ITerminalProtocol
{
    /// <summary>Sends input exactly as if typed (UTF-8; Enter is "\r").</summary>
    Task SendInputAsync(byte[] data, CancellationToken ct = default);
}

public enum SpecialKey
{
    CtrlAltDel,
    CtrlEsc,
}

/// <summary>A graphical session that can receive key combinations the local OS would intercept.</summary>
public interface ISpecialKeysProtocol
{
    IReadOnlyList<SpecialKey> SupportedSpecialKeys { get; }

    Task SendSpecialKeyAsync(SpecialKey key, CancellationToken ct = default);
}

/// <summary>Display toggles a graphical session supports while connected.</summary>
public interface IDisplayOptionsProtocol
{
    bool SupportsSmartSize { get; }

    bool SmartSize { get; set; }

    bool SupportsViewOnly { get; }

    bool ViewOnly { get; set; }
}
