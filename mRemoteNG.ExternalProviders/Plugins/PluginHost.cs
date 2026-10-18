using System.Drawing;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Core.App.Info;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Localization;
using mRemoteNG.Core.Settings;
using mRemoteNG.PluginContracts;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.ExternalProviders.Plugins;

/// <summary>What happened to a plugin found in a plugin folder.</summary>
public enum PluginState
{
    Loaded,
    /// <summary>Listed in <see cref="AppSettings.DisabledPlugins"/>.</summary>
    Disabled,
    /// <summary>Needs a newer app (<see cref="IPlugin.MinimumHostVersion"/>).</summary>
    Incompatible,
    /// <summary>Another plugin with the same id was loaded first.</summary>
    Duplicate,
    /// <summary>Uses something only the Windows app has (WinForms tool windows).</summary>
    Unsupported,
    /// <summary>The assembly or the plugin's Initialize failed.</summary>
    Failed,
}

/// <summary>A plugin (or plugin assembly) in the plugin folders and what became of it.</summary>
/// <param name="Id">The plugin id; the assembly file name when no plugin could be created.</param>
/// <param name="Kinds">"Address resolver", "Connection properties", "Tree action", "Tool window".</param>
public sealed record PluginCatalogEntry(
    string Id,
    string DisplayName,
    string Version,
    string AssemblyPath,
    IReadOnlyList<string> Kinds,
    PluginState State,
    string Message = "");

public enum PluginMessageLevel
{
    Information,
    Warning,
    Error,
}

/// <summary>A message a plugin wrote (<see cref="IMessageWriter"/>).</summary>
/// <param name="LogOnly">The plugin asked for the log only (no notification).</param>
public sealed record PluginMessage(PluginMessageLevel Level, string Text, bool LogOnly, Exception? Exception = null);

/// <summary>
/// Loads mRemoteNG plugins (mRp.Contracts) in the cross-platform app: connection property providers, address
/// resolvers and tree context actions. WinForms tool-window plugins need the Windows app and are listed as
/// unsupported. Plugins are searched in the Plugins folders next to the executable and in the data directory,
/// plus <see cref="AppSettings.PluginFolderPaths"/>; each assembly gets its own <see cref="PluginLoadContext"/>.
/// Plugins load once (<see cref="Load"/>): enabling, disabling or adding plugins takes effect after a restart.
/// </summary>
public sealed class PluginHost : IPluginContext, IConnectionImportService, IMessageWriter, IPluginResources
{
    public const string PluginsFolderName = "Plugins";

    private readonly Func<AppSettings> _settings;
    private readonly ILogger _logger;
    private readonly Version _hostVersion;
    private readonly Func<IReadOnlyList<string>> _defaultDirectories;
    private readonly List<IPlugin> _plugins = [];
    private readonly List<PluginCatalogEntry> _catalog = [];
    private readonly object _gate = new();
    private bool _loaded;

    /// <param name="hostVersion">Compared with <see cref="IPlugin.MinimumHostVersion"/>; the entry assembly's version by default.</param>
    /// <param name="defaultDirectories">The folders always searched; the Plugins folders next to the executable and in the data directory by default.</param>
    public PluginHost(Func<AppSettings> settings, ILogger<PluginHost>? logger = null, Version? hostVersion = null,
        Func<IReadOnlyList<string>>? defaultDirectories = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _hostVersion = hostVersion ?? Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0);
        _defaultDirectories = defaultDirectories ?? DefaultDirectories;
    }

    /// <summary>The plugins that loaded, in load order.</summary>
    public IReadOnlyList<IPlugin> Plugins
    {
        get
        {
            lock (_gate)
                return _plugins.ToList();
        }
    }

    /// <summary>Every plugin found, including the ones that did not load and why.</summary>
    public IReadOnlyList<PluginCatalogEntry> Catalog
    {
        get
        {
            lock (_gate)
                return _catalog.ToList();
        }
    }

    public bool IsLoaded => _loaded;

    /// <summary>Raised for every message a plugin writes (also logged).</summary>
    public event EventHandler<PluginMessage>? MessageWritten;

    /// <summary>
    /// Adds connections a plugin creates (<see cref="IConnectionImportService"/>) to the connection tree. Until the
    /// UI sets it, imports are refused with a warning.
    /// </summary>
    public Action<IReadOnlyList<ConnectionInfo>>? ImportHandler { get; set; }

    public static IReadOnlyList<string> DefaultDirectories() =>
        new[]
        {
            Path.Combine(ApplicationPaths.ExecutableDirectory, PluginsFolderName),
            Path.Combine(ApplicationPaths.SettingsDirectory, PluginsFolderName),
        }.Distinct(PathComparer).ToList();

    /// <summary>The folders searched for plugins: the defaults, then the configured ones.</summary>
    public IReadOnlyList<string> SearchDirectories()
    {
        var configured = (_settings().PluginFolderPaths ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => ApplicationPaths.FromStoredPath(p));
        return _defaultDirectories().Concat(configured).Select(Path.GetFullPath).Distinct(PathComparer).ToList();
    }

    public static HashSet<string> ParseIds(string? ids) =>
        new((ids ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Finds and initializes the plugins (once; later calls do nothing).</summary>
    public void Load()
    {
        lock (_gate)
        {
            if (_loaded)
                return;
            _loaded = true;
        }

        var disabled = ParseIds(_settings().DisabledPlugins);
        foreach (var directory in SearchDirectories())
        {
            if (!Directory.Exists(directory))
                continue;

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly);
                Array.Sort(files, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Cannot read the plugin folder {Directory}", directory);
                continue;
            }

            foreach (var file in files.Where(PluginLoadContext.ReferencesContracts))
                LoadAssembly(file, disabled);
        }

        _logger.LogInformation("Plugins: {Loaded} loaded, {Other} not loaded", _plugins.Count, _catalog.Count - _plugins.Count);
    }

    /// <summary>Loads the plugins of one assembly file (in its own load context).</summary>
    internal void LoadAssembly(string path, ISet<string> disabled)
    {
        Assembly assembly;
        try
        {
            assembly = new PluginLoadContext(path).LoadFromAssemblyPath(Path.GetFullPath(path));
        }
        catch (Exception ex)
        {
            AddIssue(path, PluginState.Failed, ex.Message);
            _logger.LogWarning(ex, "Cannot load the plugin assembly {Path}", path);
            return;
        }
        LoadPlugins(assembly, path, disabled);
    }

    /// <summary>Creates, checks and initializes every plugin type in <paramref name="assembly"/>.</summary>
    internal void LoadPlugins(Assembly assembly, string path, ISet<string> disabled)
    {
        var (types, loadErrors) = GetLoadableTypes(assembly);
        var found = 0;
        foreach (var type in types)
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IPlugin).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
                continue;
            found++;

            IPlugin plugin;
            try
            {
                plugin = (IPlugin)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                var error = (ex as TargetInvocationException)?.InnerException ?? ex;
                AddIssue(path, PluginState.Failed, error.Message, type.FullName);
                _logger.LogWarning(error, "Cannot create the plugin {Type} from {Path}", type.FullName, path);
                continue;
            }

            string id, name, version;
            try
            {
                (id, name, version) = (plugin.Id, plugin.DisplayName, plugin.Version?.ToString() ?? string.Empty);
            }
            catch (Exception ex)
            {
                AddIssue(path, PluginState.Failed, ex.Message, type.FullName);
                continue;
            }

            var state = PluginState.Loaded;
            var message = string.Empty;
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(id))
                    (state, message) = (PluginState.Failed, "The plugin has no id.");
                else if (_plugins.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)))
                    (state, message) = (PluginState.Duplicate, $"A plugin with the id {id} is already loaded.");
            }
            if (state == PluginState.Loaded && disabled.Contains(id))
                state = PluginState.Disabled;
            if (state == PluginState.Loaded && plugin.MinimumHostVersion is { } minimum && minimum > _hostVersion)
                (state, message) = (PluginState.Incompatible, $"Needs mRemoteNG {minimum} or newer.");

            if (state == PluginState.Loaded)
            {
                try
                {
                    plugin.Initialize(this);
                }
                catch (Exception ex)
                {
                    (state, message) = (PluginState.Failed, ex.Message);
                    _logger.LogWarning(ex, "The plugin {Id} failed to initialize", id);
                }
            }

            lock (_gate)
            {
                if (state == PluginState.Loaded)
                    _plugins.Add(plugin);
                _catalog.Add(new PluginCatalogEntry(id, string.IsNullOrWhiteSpace(name) ? id : name, version, path, KindsOf(plugin), state, message));
            }
            if (state == PluginState.Loaded)
                _logger.LogInformation("Loaded plugin {Id} {Version} from {Path}", id, version, path);
            else
                _logger.LogInformation("Plugin {Id} from {Path} not loaded: {State} {Message}", id, path, state, message);
        }

        if (loadErrors.Count > 0)
        {
            var unsupported = loadErrors.FirstOrDefault(e => e.Contains("IToolWindowPlugin", StringComparison.Ordinal)
                                                             || e.Contains("System.Windows.Forms", StringComparison.Ordinal));
            if (unsupported is not null)
                AddIssue(path, PluginState.Unsupported, "Tool-window (WinForms) plugins only work in the Windows app.", kinds: ["Tool window"]);
            else if (found == 0)
                AddIssue(path, PluginState.Failed, loadErrors[0]);
            _logger.LogInformation("Some types of {Path} cannot be loaded here: {Errors}", path, string.Join(" | ", loadErrors.Distinct()));
        }
    }

    private void AddIssue(string path, PluginState state, string message, string? id = null, IReadOnlyList<string>? kinds = null)
    {
        lock (_gate)
            _catalog.Add(new PluginCatalogEntry(id ?? Path.GetFileNameWithoutExtension(path), id ?? Path.GetFileNameWithoutExtension(path),
                string.Empty, path, kinds ?? [], state, message));
    }

    private static (IReadOnlyList<Type> Types, IReadOnlyList<string> Errors) GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return (assembly.GetTypes(), []);
        }
        catch (ReflectionTypeLoadException ex)
        {
            return (ex.Types.OfType<Type>().ToList(),
                ex.LoaderExceptions.OfType<Exception>().Select(e => e.Message).ToList());
        }
    }

    private static IReadOnlyList<string> KindsOf(IPlugin plugin)
    {
        var kinds = new List<string>();
        if (plugin is IConnectionAddressResolverPlugin)
            kinds.Add("Address resolver");
        if (plugin is IConnectionPropertyProviderPlugin)
            kinds.Add("Connection properties");
        if (plugin is ITreeContextActionPlugin)
            kinds.Add("Tree action");
        return kinds;
    }

    // ── What the app asks the plugins ────────────────────────────────────

    /// <summary>
    /// The connection properties the plugins define, for the connection editor (the first definition of a key
    /// wins, as in the WinForms app). A plugin that fails is skipped and logged.
    /// </summary>
    public IReadOnlyList<ConnectionPropertyDescriptor> GetConnectionProperties()
    {
        var result = new List<ConnectionPropertyDescriptor>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in Plugins.OfType<IConnectionPropertyProviderPlugin>())
        {
            IReadOnlyCollection<ConnectionPropertyDefinition> definitions;
            try
            {
                definitions = plugin.ConnectionProperties ?? [];
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The plugin {Id} failed to list its connection properties", plugin.Id);
                continue;
            }

            foreach (var definition in definitions)
            {
                if (definition is null || string.IsNullOrWhiteSpace(definition.Key) || !keys.Add(definition.Key))
                    continue;
                var editor = definition.PropertyType switch
                {
                    PluginPropertyType.Boolean => ConnectionPropertyEditor.Boolean,
                    PluginPropertyType.Integer => ConnectionPropertyEditor.Number,
                    _ => ConnectionPropertyEditor.Text,
                };
                var protocols = definition.SupportedProtocols is { Count: > 0 } ids
                    ? ids.Select(id => PluginProtocols.TryToCore(id, out var p) ? p : (Core.Connection.Protocol.ProtocolType?)null)
                        .OfType<Core.Connection.Protocol.ProtocolType>().Distinct().ToList()
                    : null;
                result.Add(ConnectionPropertyDescriptor.ForPlugin(definition.Key, definition.Category, definition.DisplayName,
                    definition.Description, editor, definition.DefaultValue, protocols));
            }
        }
        return result;
    }

    /// <summary>The tree context actions that apply to <paramref name="node"/>, by sort order.</summary>
    public IReadOnlyList<ITreeContextActionPlugin> GetTreeActions(ConnectionInfo node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var connection = new NodePluginConnection(node);
        var result = new List<(ITreeContextActionPlugin Plugin, int Order)>();
        foreach (var plugin in Plugins.OfType<ITreeContextActionPlugin>())
        {
            try
            {
                if (plugin.CanExecute(connection))
                    result.Add((plugin, plugin.TreeContextMenuAction?.SortOrder ?? 0));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The plugin {Id} failed in CanExecute", plugin.Id);
            }
        }
        return result.OrderBy(r => r.Order).Select(r => r.Plugin).ToList();
    }

    /// <summary>Runs a tree context action for <paramref name="node"/>; a failure is reported as a plugin error message.</summary>
    public bool ExecuteTreeAction(ITreeContextActionPlugin plugin, ConnectionInfo node)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(node);
        var connection = new NodePluginConnection(node);
        try
        {
            if (!plugin.CanExecute(connection))
                return false;
            plugin.Execute(connection);
            return true;
        }
        catch (Exception ex)
        {
            Exception($"{plugin.DisplayName}: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Lets every address resolver that wants to rewrite this attempt's host name (and port or credentials), in
    /// load order. The saved connection keeps its host name.
    /// </summary>
    public async Task<ConnectionParameters> ResolveAddressAsync(ConnectionInfo node, ConnectionParameters parameters, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(parameters);
        foreach (var plugin in Plugins.OfType<IConnectionAddressResolverPlugin>())
        {
            var connection = new PreparingPluginConnection(node, parameters);
            try
            {
                if (!plugin.CanResolve(connection))
                    continue;
                await plugin.ResolveAsync(connection, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ExternalProviderException($"{plugin.DisplayName}: {ex.Message}", ex);
            }

            var resolved = connection.ApplyTo(parameters);
            if (string.IsNullOrWhiteSpace(resolved.Hostname))
                throw new ExternalProviderException($"{plugin.DisplayName}: \"{node.Name}\" has no address to connect to.");
            if (resolved.Hostname != parameters.Hostname || resolved.Port != parameters.Port)
                _logger.LogInformation("Plugin {Id} resolved \"{Connection}\" to {Host}:{Port}", plugin.Id, node.Name, resolved.Hostname, resolved.Port);
            parameters = resolved;
        }
        return parameters;
    }

    // ── IPluginContext ───────────────────────────────────────────────────

    IConnectionImportService IPluginContext.Connections => this;

    IMessageWriter IPluginContext.Messages => this;

    IPluginResources IPluginContext.Resources => this;

    void IConnectionImportService.ImportConnections(IEnumerable<PluginConnectionRequest> connections)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var nodes = new List<ConnectionInfo>();
        foreach (var request in connections.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Hostname)))
        {
            if (!PluginProtocols.TryToCore(request.ProtocolId, out var protocol))
                throw new ArgumentException($"Unsupported plugin protocol '{request.ProtocolId}'.", nameof(connections));
            var node = new ConnectionInfo
            {
                Name = string.IsNullOrWhiteSpace(request.Name) ? request.Hostname.Trim() : request.Name.Trim(),
                Hostname = request.Hostname.Trim(),
                Protocol = protocol,
                Port = request.Port ?? ConnectionInfo.GetDefaultPort(protocol),
            };
            nodes.Add(node);
        }

        if (nodes.Count == 0)
            return;
        if (ImportHandler is not { } handler)
        {
            Warning($"{nodes.Count} connection(s) from a plugin were not imported: no connection file is open.");
            return;
        }
        handler(nodes);
    }

    public void Info(string message, bool logOnly = false) => Write(PluginMessageLevel.Information, message, logOnly);

    public void Warning(string message, bool logOnly = false) => Write(PluginMessageLevel.Warning, message, logOnly);

    public void Error(string message, bool logOnly = false) => Write(PluginMessageLevel.Error, message, logOnly);

    public void Exception(string message, Exception exception) => Write(PluginMessageLevel.Error, message, logOnly: false, exception);

    private void Write(PluginMessageLevel level, string message, bool logOnly, Exception? exception = null)
    {
        var logLevel = level switch
        {
            PluginMessageLevel.Error => LogLevel.Error,
            PluginMessageLevel.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        _logger.Log(logLevel, exception, "Plugin: {Message}", message);
        MessageWritten?.Invoke(this, new PluginMessage(level, message ?? string.Empty, logOnly, exception));
    }

    /// <summary>The cross-platform app has no System.Drawing images to give out.</summary>
    Image? IPluginResources.GetImage(string resourceName) => null;

    /// <summary>The app's text for <paramref name="resourceName"/> (legacy Language.resx keys too) in the UI language.</summary>
    string IPluginResources.GetString(string resourceName, string fallback) =>
        !string.IsNullOrEmpty(resourceName) && Localizer.Exists(resourceName) ? Localizer.Get(resourceName) : fallback;
}
