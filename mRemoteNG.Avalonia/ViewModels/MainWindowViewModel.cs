using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Protocols.Abstractions;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the main application window.
/// Owns top-level navigation state, docking layout, and all menu commands.
/// </summary>
public sealed class MainWindowViewModel : ReactiveObject
{
    private readonly ConnectionsService _connectionsService;
    private readonly SessionsDockable _sessions;
    private readonly LogPanelDockable _log;
    private readonly DebugConsoleDockable _debug;

    private string _title = "mRemoteNG";
    private int _activeConnectionCount;
    private string _quickConnectHost = string.Empty;
    private string _quickConnectProtocol = "SSH";

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public int ActiveConnectionCount
    {
        get => _activeConnectionCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _activeConnectionCount, value);
            Title = value > 0
                ? $"mRemoteNG — {value} active connection{(value == 1 ? "" : "s")}"
                : "mRemoteNG";
        }
    }

    public string QuickConnectHost
    {
        get => _quickConnectHost;
        set => this.RaiseAndSetIfChanged(ref _quickConnectHost, value);
    }

    public string QuickConnectProtocol
    {
        get => _quickConnectProtocol;
        set => this.RaiseAndSetIfChanged(ref _quickConnectProtocol, value);
    }

    public string[] QuickConnectProtocols { get; } = ["SSH", "RDP", "VNC", "Telnet", "HTTP", "HTTPS"];

    /// <summary>Child ViewModel for the connection tree panel.</summary>
    public ConnectionTreeViewModel ConnectionTree { get; }

    /// <summary>Sessions dock for the tabbed session area.</summary>
    public SessionsDockable Sessions { get; }

    /// <summary>Log panel dock.</summary>
    public LogPanelDockable LogPanel { get; }

    /// <summary>Debug console dock.</summary>
    public DebugConsoleDockable DebugConsole { get; }

    // ── Commands ──────────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> NewConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenConnectionFileCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveConnectionFileCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveAsConnectionFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> ExitCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenOptionsCommand { get; }
    public ReactiveCommand<Unit, Unit> QuickConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenQuickConnectDialogCommand { get; }
    public ReactiveCommand<Unit, Unit> AboutCommand { get; }
    public ReactiveCommand<Unit, Unit> PortScannerCommand { get; }

    public MainWindowViewModel(
        ConnectionTreeViewModel connectionTree,
        ConnectionsService connectionsService,
        SessionsDockable sessions,
        LogPanelDockable logPanel,
        DebugConsoleDockable debugConsole)
    {
        ConnectionTree = connectionTree;
        _connectionsService = connectionsService;
        _sessions = sessions;
        _log = logPanel;
        _debug = debugConsole;
        Sessions = sessions;
        LogPanel = logPanel;
        DebugConsole = debugConsole;

        NewConnectionCommand = ReactiveCommand.Create(OnNewConnection);
        OpenConnectionFileCommand = ReactiveCommand.CreateFromTask(OnOpenConnectionFile);
        SaveConnectionFileCommand = ReactiveCommand.Create(OnSaveConnectionFile);
        SaveAsConnectionFileCommand = ReactiveCommand.CreateFromTask(OnSaveAsConnectionFile);
        ImportCommand = ReactiveCommand.CreateFromTask(OnImport);
        ExportCommand = ReactiveCommand.CreateFromTask(OnExport);
        ExitCommand = ReactiveCommand.Create(() => System.Environment.Exit(0));
        OpenOptionsCommand = ReactiveCommand.CreateFromTask(OnOpenOptions);
        QuickConnectCommand = ReactiveCommand.CreateFromTask(OnQuickConnect);
        OpenQuickConnectDialogCommand = ReactiveCommand.CreateFromTask(OnOpenQuickConnectDialog);
        AboutCommand = ReactiveCommand.CreateFromTask(OnAbout);
        PortScannerCommand = ReactiveCommand.CreateFromTask(OnPortScanner);

        // Track active connection count
        sessions.Sessions.CollectionChanged += (_, _) =>
            ActiveConnectionCount = sessions.Sessions.Count;

        // Route any unhandled command errors to the log panel
        QuickConnectCommand.ThrownExceptions.Subscribe(ex =>
            _log.Log($"Quick connect error: {ex.Message}", LogLevel.Error));
        OpenConnectionFileCommand.ThrownExceptions.Subscribe(ex =>
            _log.Log($"Open file error: {ex.Message}", LogLevel.Error));
        OpenQuickConnectDialogCommand.ThrownExceptions.Subscribe(ex =>
            _log.Log($"Quick connect dialog error: {ex.Message}", LogLevel.Error));
    }

    private void OnNewConnection()
    {
        var vm = new ConnectionDialogViewModel();
        vm.Saved += result =>
        {
            ConnectionTree.AddConnection(result.Name, result.Protocol, result.Hostname, result.Port, result.Username);
            _log.Log($"Connection '{result.Name}' added ({result.Protocol}://{result.Hostname}:{result.Port}).");
        };

        var dialog = new ConnectionDialog(vm);
        dialog.ShowDialog(GetMainWindow());
    }

    private async Task OnOpenConnectionFile()
    {
        var window = GetMainWindow();
        if (window is null) return;
        var files = await window.StorageProvider.OpenFilePickerAsync(
            new global::Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Open Connection File",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new("mRemoteNG XML") { Patterns = ["*.xml"] },
                    new("All files") { Patterns = ["*.*"] },
                ],
            });
        if (files.Count > 0)
            await LoadConnectionFileAsync(window, files[0].Path.LocalPath);
    }

    /// <summary>Loads a connection file, prompting for the master password when the file has one.</summary>
    private async Task LoadConnectionFileAsync(global::Avalonia.Controls.Window owner, string path)
    {
        string? password = null;
        string? error = null;
        while (true)
        {
            try
            {
                ConnectionTree.LoadFromFile(path, password);
                _log.Log($"Loaded connection file: {path}");
                Title = $"mRemoteNG — {System.IO.Path.GetFileName(path)}";
                return;
            }
            catch (ConnectionFilePasswordException ex)
            {
                if (ex.PasswordWasSupplied)
                    error = "Incorrect password. Please try again.";

                var prompt = new PasswordPromptDialog(
                    $"\"{System.IO.Path.GetFileName(path)}\" is protected by a password.", error);
                password = await prompt.ShowDialog<string?>(owner);
                if (password is null)
                {
                    _log.Log($"Opening {path} cancelled: password required.", LogLevel.Warning);
                    return;
                }
            }
            catch (Exception ex)
            {
                _log.Log($"Failed to load connection file: {ex.Message}", LogLevel.Error);
                return;
            }
        }
    }

    private void OnSaveConnectionFile()
    {
        try
        {
            if (_connectionsService.CurrentFilePath is null)
            {
                // No file loaded yet — trigger Save As
                _ = OnSaveAsConnectionFile();
                return;
            }
            ConnectionTree.SaveToFile();
            _log.Log($"Saved connection file: {_connectionsService.CurrentFilePath}");
        }
        catch (Exception ex)
        {
            _log.Log($"Failed to save connection file: {ex.Message}", LogLevel.Error);
        }
    }

    private async Task OnSaveAsConnectionFile()
    {
        var window = GetMainWindow();
        if (window is null) return;
        var file = await window.StorageProvider.SaveFilePickerAsync(
            new global::Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Save Connection File",
                DefaultExtension = "xml",
                FileTypeChoices =
                [
                    new("mRemoteNG XML") { Patterns = ["*.xml"] },
                ],
            });
        if (file is not null)
        {
            try
            {
                ConnectionTree.SaveToFile(file.Path.LocalPath);
                _log.Log($"Saved connection file: {file.Path.LocalPath}");
                Title = $"mRemoteNG — {file.Name}";
            }
            catch (Exception ex)
            {
                _log.Log($"Failed to save connection file: {ex.Message}", LogLevel.Error);
            }
        }
    }

    private async Task OnImport()
    {
        var window = GetMainWindow();
        if (window is null) return;

        var selectedFolder = GetSelectedImportExportFolder();
        var request = await new ImportDialog(selectedFolder?.Name).ShowDialog<ImportRequest?>(window);
        if (request is null) return;

        var targetNode = request.IntoSelectedFolder && selectedFolder is not null
            ? selectedFolder
            : ConnectionTree.Nodes.FirstOrDefault();
        if (targetNode?.Model is not global::mRemoteNG.Core.Container.ContainerInfo targetContainer)
        {
            _log.Log("Import failed: there is no connection tree to import into.", LogLevel.Error);
            return;
        }

        var result = await RunImportAsync(window, request, targetContainer);
        if (result is null) return;

        // The tree view mirrors the model; add view nodes for the imported model nodes so the
        // import shows up and is kept when the tree is saved.
        foreach (var node in result.ImportedNodes)
            targetNode.Children.Add(ConnectionNodeViewModel.FromModel(node));
        targetNode.IsExpanded = true;
        ConnectionTree.SetDependencies(_sessions, AppServices.GetRequired<IProtocolFactory>());

        var sourceName = global::mRemoteNG.Core.Config.Import.ImportSourceDescriptor.For(request.Type).DisplayName;
        var from = string.IsNullOrEmpty(request.Source) ? sourceName : $"{sourceName} \"{request.Source}\"";
        _log.Log($"Imported {result.Summary} from {from} into \"{targetContainer.Name}\". Save the connection file to keep them.");
        foreach (var warning in result.Warnings)
            _log.Log($"Import: {warning}", LogLevel.Warning);
    }

    /// <summary>Runs the import, asking for the password of a protected mRemoteNG file. Returns null when it failed or was cancelled.</summary>
    private async Task<global::mRemoteNG.Core.Config.Import.ImportResult?> RunImportAsync(
        Window owner,
        ImportRequest request,
        global::mRemoteNG.Core.Container.ContainerInfo targetContainer)
    {
        var importService = new global::mRemoteNG.Core.Config.Import.ConnectionImportService(
            AppServices.GetRequired<global::mRemoteNG.Core.Security.Factories.ICryptoProviderFactory>());
        string? password = null;

        while (true)
        {
            try
            {
                if (request.Type == global::mRemoteNG.Core.Config.Import.ImportSourceType.PuttySessions
                    && string.IsNullOrEmpty(request.Source)
                    && OperatingSystem.IsWindows())
                {
                    // PuTTY keeps sessions in the registry on Windows; that is read by the platform provider.
                    if (AppServices.Provider.GetService(typeof(global::mRemoteNG.Platform.IPuttySessionsProvider))
                        is not global::mRemoteNG.Platform.IPuttySessionsProvider puttyProvider)
                    {
                        _log.Log("Import failed: reading PuTTY sessions from the registry is not available. Select a sessions folder instead.", LogLevel.Error);
                        return null;
                    }
                    var sessions = await puttyProvider.GetSessionsAsync();
                    return importService.ImportPuttySessions(sessions, targetContainer);
                }

                return importService.Import(request.Type, request.Source, targetContainer, password);
            }
            catch (ConnectionFilePasswordException ex)
            {
                var fileName = System.IO.Path.GetFileName(request.Source);
                var prompt = new PasswordPromptDialog(
                    $"\"{fileName}\" is protected by a password.",
                    ex.PasswordWasSupplied ? "Incorrect password. Please try again." : null);
                password = await prompt.ShowDialog<string?>(owner);
                if (password is null)
                {
                    _log.Log($"Import of {request.Source} cancelled: password required.", LogLevel.Warning);
                    return null;
                }
            }
            catch (Exception ex)
            {
                _log.Log($"Import failed: {ex.Message}", LogLevel.Error);
                return null;
            }
        }
    }

    /// <summary>
    /// The folder selected in the tree (or the folder of the selected connection) for import/export;
    /// null when nothing below the root is selected.
    /// </summary>
    private ConnectionNodeViewModel? GetSelectedImportExportFolder()
    {
        var selected = ConnectionTree.SelectedNode;
        if (selected?.Model is null || ConnectionTree.Nodes.Contains(selected))
            return null;
        if (selected.IsFolder)
            return selected;

        var parentModel = selected.Model.Parent;
        if (parentModel is null || parentModel is global::mRemoteNG.Core.Tree.Root.RootNodeInfo)
            return null;
        return FindNodeForModel(ConnectionTree.Nodes, parentModel);
    }

    private static ConnectionNodeViewModel? FindNodeForModel(
        IEnumerable<ConnectionNodeViewModel> nodes,
        global::mRemoteNG.Core.Connection.ConnectionInfo model)
    {
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node.Model, model))
                return node;
            var found = FindNodeForModel(node.Children, model);
            if (found is not null)
                return found;
        }
        return null;
    }

    private async Task OnExport()
    {
        var window = GetMainWindow();
        if (window is null) return;

        var selectedFolder = GetSelectedImportExportFolder();
        var request = await new ExportDialog(selectedFolder?.Name).ShowDialog<ExportRequest?>(window);
        if (request is null) return;

        global::mRemoteNG.Core.Connection.ConnectionInfo? exportTarget = request.SelectedFolderOnly
            ? selectedFolder?.Model
            : _connectionsService.ConnectionTreeModel?.RootNode;
        if (exportTarget is null)
        {
            _log.Log("Export failed: nothing to export.", LogLevel.Error);
            return;
        }

        try
        {
            var exporter = new global::mRemoteNG.Core.Config.Export.ConnectionExporter(
                AppServices.GetRequired<global::mRemoteNG.Core.Security.Factories.ICryptoProviderFactory>());
            exporter.ExportToFile(request.FilePath, exportTarget, new global::mRemoteNG.Core.Config.Export.ExportOptions
            {
                Format = request.Format,
                SaveFilter = request.SaveFilter,
                Password = request.Password,
                // Same cipher settings as the open connection file.
                Encryption = _connectionsService.Encryption,
            });

            var count = exportTarget is global::mRemoteNG.Core.Container.ContainerInfo container
                ? container.GetRecursiveChildList().Count(n => n is not global::mRemoteNG.Core.Container.ContainerInfo)
                : 1;
            var protection = request.Password is null ? "" : ", password protected";
            _log.Log($"Exported {count} connection{(count == 1 ? "" : "s")} to {request.FilePath} ({request.Format}{protection}).");
        }
        catch (Exception ex)
        {
            _log.Log($"Export failed: {ex.Message}", LogLevel.Error);
        }
    }

    private async Task OnOpenOptions()
    {
        var dialog = new OptionsWindow();
        await dialog.ShowDialog(GetMainWindow());
    }

    private async Task OnQuickConnect()
    {
        if (string.IsNullOrWhiteSpace(QuickConnectHost)) return;

        try
        {
            var protocolType = ConnectionNodeViewModel.ResolveProtocolType(QuickConnectProtocol);

            // Parse host:port format
            var host = QuickConnectHost;
            var port = ConnectionNodeViewModel.DefaultPortFor(protocolType);
            var colonIdx = host.LastIndexOf(':');
            if (colonIdx > 0 && int.TryParse(host[(colonIdx + 1)..], out var parsedPort))
            {
                host = host[..colonIdx];
                port = parsedPort;
            }

            var parameters = new ConnectionParameters
            {
                Hostname = host,
                Port = port,
                Protocol = protocolType,
            };

            var factory = AppServices.GetRequired<IProtocolFactory>();
            await _sessions.OpenConnectionAsync(parameters, factory);

            _log.Log($"Quick connect: {QuickConnectProtocol}://{host}:{port}");
            QuickConnectHost = string.Empty;
        }
        catch (Exception ex)
        {
            _log.Log($"Quick connect failed: {ex.Message}", LogLevel.Error);
        }
    }

    private async Task OnOpenQuickConnectDialog()
    {
        var dialog = new QuickConnectDialog();
        var result = await dialog.ShowDialog<QuickConnectResult?>(GetMainWindow());

        if (result is not null)
        {
            QuickConnectHost = result.Hostname;
            QuickConnectProtocol = result.Protocol;
            await OnQuickConnect();
        }
    }

    private async Task OnAbout()
    {
        var dialog = new AboutDialog();
        await dialog.ShowDialog(GetMainWindow());
    }

    private async Task OnPortScanner()
    {
        var dialog = new PortScannerDialog();
        await dialog.ShowDialog(GetMainWindow());
    }

    private static Window? GetMainWindow() =>
        (global::Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
