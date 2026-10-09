using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.External;

/// <summary>
/// Runs a connection's "External tool before" (<see cref="ConnectionInfo.PreExtApp"/>) before it connects and its
/// "External tool after" (<see cref="ConnectionInfo.PostExtApp"/>) when the session tab closes, like the legacy
/// ConnectionInitiator:
/// <list type="bullet">
/// <item>the "before" tool runs with the connection's variables; with "wait for exit" the connection waits until
/// it exits (cancelling the connect stops the wait, not the tool);</item>
/// <item>a missing or failing "before" tool is reported but does not prevent the connection (legacy behaviour);</item>
/// <item>the "after" tool is registered as a session resource, so it runs when the session's resources are released.</item>
/// </list>
/// </summary>
public sealed class ExternalToolPreparationStep(ExternalToolsService tools, ILogger<ExternalToolPreparationStep>? logger = null)
    : IConnectionPreparationStep
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public int Order => 400;

    public async Task<ConnectionParameters> PrepareAsync(PreparationContext context, ConnectionParameters parameters, CancellationToken ct)
    {
        var connection = context.Connection;

        string preApp = connection.PreExtApp;
        if (!string.IsNullOrEmpty(preApp))
        {
            _logger.LogDebug("Running pre-connection tool {Tool} for {Connection}", preApp, connection.Name);
            await tools.RunAsync(preApp, connection, ct);
        }

        string postApp = connection.PostExtApp;
        if (!string.IsNullOrEmpty(postApp))
            context.Resources.Add(new PostConnectionTool(tools, postApp, connection.Clone(), _logger));

        return parameters;
    }

    /// <summary>Runs the "after" tool when the session closes (with the connection as it was when the session opened).</summary>
    internal sealed class PostConnectionTool(ExternalToolsService tools, string toolName, ConnectionInfo connection, ILogger logger)
        : IAsyncDisposable
    {
        private int _disposed;

        public string ToolName { get; } = toolName;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            logger.LogDebug("Running post-connection tool {Tool} for {Connection}", ToolName, connection.Name);
            await tools.RunAsync(ToolName, connection);
        }
    }
}
