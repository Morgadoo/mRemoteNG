using System.Collections.ObjectModel;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tools;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Protocols.External;

public enum ExternalToolMessageLevel
{
    Info,
    Warning,
    Error,
}

/// <summary>A message for the application log from <see cref="ExternalToolsService"/>.</summary>
public sealed class ExternalToolMessageEventArgs(ExternalToolMessageLevel level, string message) : EventArgs
{
    public ExternalToolMessageLevel Level { get; } = level;

    public string Message { get; } = message;
}

/// <summary>
/// The user's external tools (loaded from / saved to <c>extApps.xml</c>) and the entry point for running one
/// against a connection — from the External Tools window, the toolbar, the tree's context menu and the
/// pre-/post-connection actions. Results and failures are reported through <see cref="Message"/> (the log panel)
/// and the logger; running a tool never throws (except on cancellation).
/// </summary>
public sealed class ExternalToolsService
{
    /// <summary>Panel that integrated tools started from the tools menu open in (legacy: Language._Tools).</summary>
    public const string ToolsPanel = "Tools";

    private readonly ExternalToolsRepository _repository;
    private readonly ExternalToolLauncher _launcher;
    private readonly ILogger _logger;
    private readonly ObservableCollection<ExternalTool> _tools = [];
    private bool _loaded;

    public ExternalToolsService(ExternalToolsRepository repository, ExternalToolLauncher launcher, ILogger<ExternalToolsService>? logger = null)
    {
        _repository = repository;
        _launcher = launcher;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Log messages meant for the user (tool started, failed, not found…). May be raised on any thread.</summary>
    public event EventHandler<ExternalToolMessageEventArgs>? Message;

    /// <summary>Raised after the tool list was replaced or saved (toolbars and menus rebuild themselves).</summary>
    public event EventHandler? ToolsChanged;

    public ExternalToolsRepository Repository => _repository;

    public ExternalToolLauncher Launcher => _launcher;

    /// <summary>The tools, loaded on first access. Edit through <see cref="ReplaceAll"/> to persist changes.</summary>
    public ObservableCollection<ExternalTool> Tools
    {
        get
        {
            EnsureLoaded();
            return _tools;
        }
    }

    /// <summary>
    /// Opens a session tab for an integrated tool (a clone of the connection with protocol IntApp). Set by the
    /// application; without it integrated tools are started in their own window.
    /// </summary>
    public Func<ConnectionInfo, Task>? OpenIntegratedSession { get; set; }

    /// <summary>Adjusts the variables of a connection before they are used (e.g. default user name from the settings).</summary>
    public Func<ExternalToolVariables, ExternalToolVariables>? VariablesFilter { get; set; }

    /// <summary>
    /// Loads <c>extApps.xml</c>; when it does not exist the default tools for this OS are used (and saved on the
    /// first <see cref="Save"/>). A damaged file is reported and leaves the list empty.
    /// </summary>
    public void Load()
    {
        _loaded = true;
        _tools.Clear();
        List<ExternalTool>? tools;
        try
        {
            tools = _repository.Load();
            if (tools is not null)
                Report(ExternalToolMessageLevel.Info, $"Loaded {tools.Count} external tools from {_repository.FilePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            _logger.LogError(ex, "Could not load external tools from {Path}", _repository.FilePath);
            Report(ExternalToolMessageLevel.Error, $"Could not load external tools from {_repository.FilePath}: {ex.Message}");
            tools = [];
        }

        foreach (var tool in tools ?? ExternalToolDefaults.Create())
            _tools.Add(tool);
        ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void EnsureLoaded()
    {
        if (!_loaded)
            Load();
    }

    /// <summary>Writes the tools to <c>extApps.xml</c>. Returns false (and reports why) when that fails.</summary>
    public bool Save()
    {
        EnsureLoaded();
        try
        {
            _repository.Save(_tools);
            ToolsChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save external tools to {Path}", _repository.FilePath);
            Report(ExternalToolMessageLevel.Error, $"Could not save external tools to {_repository.FilePath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Replaces the whole list (e.g. after editing in the External Tools window) and saves it.</summary>
    public bool ReplaceAll(IEnumerable<ExternalTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var list = tools.ToList();
        _loaded = true;
        _tools.Clear();
        foreach (var tool in list)
            _tools.Add(tool);
        return Save();
    }

    /// <summary>Finds a tool by its display name (exact match first, as the legacy app, then ignoring case).</summary>
    public ExternalTool? Find(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;
        return Tools.FirstOrDefault(t => t.DisplayName == name)
            ?? Tools.FirstOrDefault(t => string.Equals(t.DisplayName, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The tools to show on the toolbar (marked "show on toolbar" and meant for this OS).</summary>
    public IEnumerable<ExternalTool> ToolbarTools => Tools.Where(t => t.ShowOnToolbar && t.IsAvailableOnCurrentPlatform);

    /// <summary>The variables of <paramref name="connection"/> after <see cref="VariablesFilter"/>; null without a connection.</summary>
    public ExternalToolVariables? GetVariables(ConnectionInfo? connection)
    {
        if (connection is null)
            return null;
        var variables = ExternalToolVariables.FromConnection(connection);
        return VariablesFilter?.Invoke(variables) ?? variables;
    }

    /// <summary>
    /// Runs <paramref name="tool"/> for <paramref name="connection"/> (null: no connection; a folder: every
    /// connection in it, as the legacy app does). Integrated tools open a session tab; others start a process and,
    /// with "wait for exit", complete when it exits. Returns false when the tool could not be started.
    /// </summary>
    public async Task<bool> RunAsync(ExternalTool tool, ConnectionInfo? connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (connection is ContainerInfo container)
        {
            if (container.Children.Count == 0)
            {
                Report(ExternalToolMessageLevel.Warning,
                    $"\"{container.Name}\" contains no connections, so \"{tool.DisplayName}\" was not started. Select a connection.");
                return false;
            }
            bool all = true;
            foreach (var child in container.Children.ToList())
                all &= await RunAsync(tool, child, ct);
            return all;
        }

        if (!tool.IsAvailableOnCurrentPlatform)
        {
            Report(ExternalToolMessageLevel.Warning,
                $"The external tool \"{tool.DisplayName}\" is meant for {tool.Platform} and was not started.");
            return false;
        }

        if (tool.TryIntegrate && OpenIntegratedSession is { } openSession)
        {
            try
            {
                await openSession(BuildIntegratedConnection(tool, connection));
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Opening integrated tool {Tool} failed", tool.DisplayName);
                Report(ExternalToolMessageLevel.Error, $"Could not open \"{tool.DisplayName}\" in a tab: {ex.Message}");
                return false;
            }
        }

        string target = connection is null ? string.Empty : $" for \"{connection.Name}\"";
        try
        {
            Report(ExternalToolMessageLevel.Info, $"Starting external tool \"{tool.DisplayName}\"{target}");
            int? exitCode = await _launcher.RunAsync(tool, GetVariables(connection), ct);
            if (exitCode is { } code)
                Report(code == 0 ? ExternalToolMessageLevel.Info : ExternalToolMessageLevel.Warning,
                    $"External tool \"{tool.DisplayName}\" exited with code {code}");
            return true;
        }
        catch (ExternalToolException ex)
        {
            _logger.LogWarning(ex, "External tool {Tool} failed", tool.DisplayName);
            Report(ExternalToolMessageLevel.Error, ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "External tool {Tool} failed", tool.DisplayName);
            Report(ExternalToolMessageLevel.Error, $"External tool \"{tool.DisplayName}\" failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Runs the tool named <paramref name="toolName"/>; reports and returns false when no such tool exists.</summary>
    public Task<bool> RunAsync(string toolName, ConnectionInfo? connection, CancellationToken ct = default)
    {
        var tool = Find(toolName);
        if (tool is null)
        {
            Report(ExternalToolMessageLevel.Warning, $"Could not find the external tool \"{toolName}\".");
            return Task.FromResult(false);
        }
        return RunAsync(tool, connection, ct);
    }

    /// <summary>
    /// The connection an integrated tool's tab is opened for (legacy ExternalTool.BuildConnectionInfoForIntegratedApp):
    /// a copy of the connection with protocol IntApp, the tool as ExtApp and the tool's name.
    /// </summary>
    public static ConnectionInfo BuildIntegratedConnection(ExternalTool tool, ConnectionInfo? connection)
    {
        var info = connection is null ? new ConnectionInfo() : connection.Clone();
        info.Protocol = CoreProtocol.IntApp;
        info.ExtApp = tool.DisplayName;
        info.Name = tool.DisplayName;
        info.Panel = ToolsPanel;
        // The tab is not the connection itself: running its before/after tools again would repeat them (or, for an
        // integrated "before" tool, open tabs endlessly).
        info.PreExtApp = string.Empty;
        info.PostExtApp = string.Empty;
        return info;
    }

    internal void Report(ExternalToolMessageLevel level, string message) =>
        Message?.Invoke(this, new ExternalToolMessageEventArgs(level, message));
}
