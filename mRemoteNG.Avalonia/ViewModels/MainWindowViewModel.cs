using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;
using mRemoteNG.Protocols.Abstractions;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;
using CoreProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>Answer to the "save changes?" prompt.</summary>
public enum UnsavedChangesChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>
/// ViewModel for the main application window.
/// Owns top-level navigation state, docking layout, and all menu commands.
/// </summary>
public sealed class MainWindowViewModel : ReactiveObject
{
    public const string GitHubUrl = "https://github.com/mRemoteNG/mRemoteNG";
    public const string DocumentationUrl = "https://mremoteng.readthedocs.io";
    public const string ReportBugUrl = "https://github.com/mRemoteNG/mRemoteNG/issues/new";

    private readonly ConnectionsService _connectionsService;
    private readonly SessionsDockable _sessions;
    private readonly LogPanelDockable _log;
    private readonly DebugConsoleDockable _debug;

    private string _title = "mRemoteNG";
    private int _activeConnectionCount;
    private string _quickConnectHost = string.Empty;
    private CoreProtocolType _quickConnectProtocol = CoreProtocolType.SSH2;
    private bool _isConnectionTreeVisible = true;
    private bool _isLogPanelVisible = true;
    private bool _isFullScreen;

    public string Title
    {
        get => _title;
        private set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public int ActiveConnectionCount
    {
        get => _activeConnectionCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _activeConnectionCount, value);
            UpdateTitle();
        }
    }

    public string QuickConnectHost
    {
        get => _quickConnectHost;
        set => this.RaiseAndSetIfChanged(ref _quickConnectHost, value);
    }

    public CoreProtocolType QuickConnectProtocol
    {
        get => _quickConnectProtocol;
        set => this.RaiseAndSetIfChanged(ref _quickConnectProtocol, value);
    }

    public CoreProtocolType[] QuickConnectProtocols { get; } = QuickConnectViewModel.SupportedProtocols;

    /// <summary>View → Connection Tree.</summary>
    public bool IsConnectionTreeVisible
    {
        get => _isConnectionTreeVisible;
        set => this.RaiseAndSetIfChanged(ref _isConnectionTreeVisible, value);
    }

    /// <summary>View → Log Panel (the bottom Log / Terminal / Debug Console area).</summary>
    public bool IsLogPanelVisible
    {
        get => _isLogPanelVisible;
        set => this.RaiseAndSetIfChanged(ref _isLogPanelVisible, value);
    }

    /// <summary>View → Full Screen (F11).</summary>
    public bool IsFullScreen
    {
        get => _isFullScreen;
        set => this.RaiseAndSetIfChanged(ref _isFullScreen, value);
    }

    /// <summary>Raised by View → Reset Layout; the view restores panel sizes.</summary>
    public event EventHandler? LayoutResetRequested;

    /// <summary>Status-bar text describing the open connection file.</summary>
    public string FileStatus => ConnectionTree.CurrentFilePath ?? "New connection file (not saved yet)";

    /// <summary>Child ViewModel for the connection tree panel.</summary>
    public ConnectionTreeViewModel ConnectionTree { get; }

    /// <summary>Sessions dock for the tabbed session area.</summary>
    public SessionsDockable Sessions { get; }

    /// <summary>Log panel dock.</summary>
    public LogPanelDockable LogPanel { get; }

    /// <summary>Debug console dock.</summary>
    public DebugConsoleDockable DebugConsole { get; }

    // ── Commands ──────────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> NewFileCommand { get; }
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
    public ReactiveCommand<Unit, Unit> OpenSftpCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleConnectionTreeCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleLogPanelCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetLayoutCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleFullScreenCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenGitHubCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDocumentationCommand { get; }
    public ReactiveCommand<Unit, Unit> ReportBugCommand { get; }
    public ReactiveCommand<Unit, Unit> CheckForUpdatesCommand { get; }

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

        NewFileCommand = ReactiveCommand.CreateFromTask(OnNewFile);
        NewConnectionCommand = ReactiveCommand.CreateFromObservable(() => ConnectionTree.NewConnectionCommand.Execute());
        OpenConnectionFileCommand = ReactiveCommand.CreateFromTask(OnOpenConnectionFile);
        SaveConnectionFileCommand = ReactiveCommand.CreateFromTask(async () => { await SaveAsync(); });
        SaveAsConnectionFileCommand = ReactiveCommand.CreateFromTask(async () => { await SaveAsAsync(); });
        ImportCommand = ReactiveCommand.CreateFromTask(OnImport);
        ExportCommand = ReactiveCommand.CreateFromTask(OnExport);
        ExitCommand = ReactiveCommand.Create(() => GetMainWindow()?.Close());
        OpenOptionsCommand = ReactiveCommand.CreateFromTask(OnOpenOptions);
        QuickConnectCommand = ReactiveCommand.CreateFromTask(OnQuickConnect);
        OpenQuickConnectDialogCommand = ReactiveCommand.CreateFromTask(OnOpenQuickConnectDialog);
        AboutCommand = ReactiveCommand.CreateFromTask(OnAbout);
        PortScannerCommand = ReactiveCommand.CreateFromTask(OnPortScanner);
        OpenSftpCommand = ReactiveCommand.CreateFromTask(OnOpenSftp);
        ToggleConnectionTreeCommand = ReactiveCommand.Create(() => { IsConnectionTreeVisible = !IsConnectionTreeVisible; });
        ToggleLogPanelCommand = ReactiveCommand.Create(() => { IsLogPanelVisible = !IsLogPanelVisible; });
        ResetLayoutCommand = ReactiveCommand.Create(OnResetLayout);
        ToggleFullScreenCommand = ReactiveCommand.Create(() => { IsFullScreen = !IsFullScreen; });
        OpenGitHubCommand = ReactiveCommand.CreateFromTask(() => OpenUrlAsync(GitHubUrl));
        OpenDocumentationCommand = ReactiveCommand.CreateFromTask(() => OpenUrlAsync(DocumentationUrl));
        ReportBugCommand = ReactiveCommand.CreateFromTask(() => OpenUrlAsync(ReportBugUrl));
        // No update service exists yet in the cross-platform app; show the releases page instead.
        CheckForUpdatesCommand = ReactiveCommand.CreateFromTask(OnCheckForUpdatesAsync);

        // Track active connection count
        sessions.Sessions.CollectionChanged += (_, _) =>
            ActiveConnectionCount = sessions.Sessions.Count;

        // Title and status bar follow the file name and unsaved-changes state.
        ConnectionTree.WhenAnyValue(t => t.IsDirty, t => t.CurrentFilePath)
            .Subscribe(_ =>
            {
                UpdateTitle();
                this.RaisePropertyChanged(nameof(FileStatus));
            });

        // Route any unhandled command errors to the log panel
        foreach (var command in new IHandleObservableErrors[]
                 {
                     NewFileCommand, NewConnectionCommand, OpenConnectionFileCommand, SaveConnectionFileCommand,
                     SaveAsConnectionFileCommand, ImportCommand, ExportCommand, OpenOptionsCommand,
                     QuickConnectCommand, OpenQuickConnectDialogCommand, AboutCommand, PortScannerCommand,
                     OpenSftpCommand, OpenGitHubCommand, OpenDocumentationCommand, ReportBugCommand,
                     CheckForUpdatesCommand,
                 })
        {
            command.ThrownExceptions.Subscribe(ex => _log.Log($"Error: {ex.Message}", LogLevel.Error));
        }
    }

    private void UpdateTitle()
    {
        var path = ConnectionTree.CurrentFilePath;
        var document = path is null ? "Untitled" : System.IO.Path.GetFileName(path);
        var title = $"mRemoteNG — {document}{(ConnectionTree.IsDirty ? "*" : "")}";
        if (ActiveConnectionCount > 0)
            title += $" ({ActiveConnectionCount} active connection{(ActiveConnectionCount == 1 ? "" : "s")})";
        Title = title;
    }

    // ── File ──────────────────────────────────────────────────────────────

    private async Task OnNewFile()
    {
        if (!await ConfirmDiscardOrSaveAsync()) return;
        ConnectionTree.CreateNewTree();
        _log.Log("Started a new connection file.");
    }

    private async Task OnOpenConnectionFile()
    {
        var window = GetMainWindow();
        if (window is null) return;
        if (!await ConfirmDiscardOrSaveAsync()) return;

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

    /// <summary>Opens the connection file given on the command line (if any) once the window is shown.</summary>
    public async Task OpenStartupFileAsync(string? path)
    {
        // A file on the command line wins; otherwise the startup setting (last file or a fixed one).
        if (string.IsNullOrWhiteSpace(path))
            path = AppServices.GetRequired<StartupService>().GetFileToOpenAtStartup();
        if (string.IsNullOrWhiteSpace(path)) return;
        var window = GetMainWindow();
        if (window is null) return;

        var fullPath = System.IO.Path.GetFullPath(path);
        if (!System.IO.File.Exists(fullPath))
        {
            _log.Log($"Connection file not found: {fullPath}", LogLevel.Error);
            return;
        }
        await LoadConnectionFileAsync(window, fullPath);
    }

    /// <summary>Loads a connection file, prompting for the master password when the file has one.</summary>
    private async Task LoadConnectionFileAsync(Window owner, string path)
    {
        string? password = null;
        string? error = null;
        while (true)
        {
            try
            {
                ConnectionTree.LoadFromFile(path, password);
                _log.Log($"Loaded connection file: {path}");
                RememberOpenFile();
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

    /// <summary>Saves to the current file, or asks for one. Returns true when the tree was saved.</summary>
    public async Task<bool> SaveAsync()
    {
        if (ConnectionTree.CurrentFilePath is null)
            return await SaveAsAsync();

        try
        {
            ConnectionTree.SaveToFile();
            _log.Log($"Saved connection file: {ConnectionTree.CurrentFilePath}");
            RememberOpenFile();
            return true;
        }
        catch (Exception ex)
        {
            _log.Log($"Failed to save connection file: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>Asks for a file name and saves. Returns true when the tree was saved.</summary>
    public async Task<bool> SaveAsAsync()
    {
        var window = GetMainWindow();
        if (window is null) return false;
        var file = await window.StorageProvider.SaveFilePickerAsync(
            new global::Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Save Connection File",
                DefaultExtension = "xml",
                SuggestedFileName = ConnectionTree.CurrentFilePath is { } current
                    ? System.IO.Path.GetFileName(current)
                    : "confCons.xml",
                FileTypeChoices =
                [
                    new("mRemoteNG XML") { Patterns = ["*.xml"] },
                ],
            });
        if (file is null) return false;

        try
        {
            ConnectionTree.SaveToFile(file.Path.LocalPath);
            _log.Log($"Saved connection file: {file.Path.LocalPath}");
            RememberOpenFile();
            return true;
        }
        catch (Exception ex)
        {
            _log.Log($"Failed to save connection file: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>
    /// When the tree has unsaved changes, asks Save / Don't Save / Cancel.
    /// Returns true when the caller may continue (saved or discarded), false to abort.
    /// </summary>
    /// <summary>Records the current file for "open last file at startup"; failures only get logged.</summary>
    private void RememberOpenFile()
    {
        try
        {
            AppServices.GetRequired<StartupService>().RecordOpenFile(ConnectionTree.CurrentFilePath);
        }
        catch (Exception ex)
        {
            _log.Log($"Could not remember the connection file: {ex.Message}", LogLevel.Warning);
        }
    }

    private async Task OnCheckForUpdatesAsync()
    {
        var settings = AppServices.GetRequired<AppSettingsService>().Current;
        var result = await AppServices.GetRequired<Services.UpdateCheckService>().CheckAsync(settings.UpdateChannel);
        _log.Log(result.Message, result.Succeeded ? LogLevel.Info : LogLevel.Warning);

        var window = GetMainWindow();
        if (window is null) return;
        if (result is { IsUpdateAvailable: true, ReleaseUrl: { } url })
        {
            if (await MessageDialog.ConfirmAsync(window, "Update available", $"{result.Message}\n\nOpen the release page?", "Open", "Later"))
                await OpenUrlAsync(url);
        }
        else
        {
            await new MessageDialog("Check for Updates", result.Message,
                new MessageDialogButton("OK", "ok", IsDefault: true, IsCancel: true)).ShowDialog<string?>(window);
        }
    }

    /// <summary>
    /// Everything that must be confirmed before the app exits, in order: unsaved changes (unless
    /// "save on exit" will save them), then open connections. Returns true when the app may exit.
    /// </summary>
    public async Task<bool> ConfirmExitAsync()
    {
        var settings = AppServices.GetRequired<AppSettingsService>().Current;
        var savedOnExit = settings.SaveConnectionsOnExit && ConnectionTree.CurrentFilePath is not null;
        if (!savedOnExit && !await ConfirmDiscardOrSaveAsync())
            return false;

        var window = GetMainWindow();
        return window is null
            || await AppServices.GetRequired<Services.CloseConfirmationService>().ConfirmExitAsync(window, _sessions.Sessions.Count);
    }

    public async Task<bool> ConfirmDiscardOrSaveAsync()
    {
        if (!ConnectionTree.IsDirty) return true;
        var window = GetMainWindow();
        if (window is null) return true;

        var document = ConnectionTree.CurrentFilePath is { } path ? System.IO.Path.GetFileName(path) : "the new connection file";
        var dialog = new MessageDialog("Unsaved Changes",
            $"Do you want to save the changes to {document}?",
            new MessageDialogButton("Cancel", nameof(UnsavedChangesChoice.Cancel), IsCancel: true),
            new MessageDialogButton("Don't Save", nameof(UnsavedChangesChoice.Discard)),
            new MessageDialogButton("Save", nameof(UnsavedChangesChoice.Save), IsDefault: true));
        var answer = await dialog.ShowDialog<string?>(window);

        return answer switch
        {
            nameof(UnsavedChangesChoice.Save) => await SaveAsync(),
            nameof(UnsavedChangesChoice.Discard) => true,
            _ => false,
        };
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

        // The import service added the nodes to the Core model; the tree view follows model
        // changes, so only expand the target and flag the file as unsaved.
        targetNode.IsExpanded = true;
        ConnectionTree.MarkDirty();

        var sourceName = global::mRemoteNG.Core.Config.Import.ImportSourceDescriptor.For(request.Type).DisplayName;
        var from = string.IsNullOrEmpty(request.Source) ? sourceName : $"{sourceName} \"{request.Source}\"";
        _log.Log($"Imported {result.Summary} from {from} into \"{targetContainer.Name}\".");
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
        var owner = GetMainWindow();
        if (owner is null) return;
        var dialog = new OptionsWindow();
        await dialog.ShowDialog(owner);
    }

    // ── Quick connect ─────────────────────────────────────────────────────

    private async Task OnQuickConnect()
    {
        if (string.IsNullOrWhiteSpace(QuickConnectHost)) return;
        await QuickConnectAsync(QuickConnectHost, QuickConnectProtocol, null, null);
    }

    private async Task QuickConnectAsync(string hostInput, CoreProtocolType protocol, string? username, string? password)
    {
        try
        {
            var (host, port, portSpecified) = QuickConnectViewModel.ParseHost(hostInput, protocol);
            // Without an explicit port, use the Options > Connections default for this protocol.
            if (!portSpecified && AppServices.GetRequired<AppSettingsService>().Current.GetDefaultPort(protocol) is { } configuredPort)
                port = configuredPort;
            var info = ConnectionDefaults.ApplyNewConnectionDefaults(new ConnectionInfo());
            info.Name = host;
            info.IsQuickConnect = true;
            info.Protocol = protocol;
            info.Hostname = host;
            info.Port = port;
            info.Username = username ?? string.Empty;
            info.Password = password ?? string.Empty;

            var factory = AppServices.GetRequired<IProtocolFactory>();
            await _sessions.OpenConnectionAsync(info, factory);

            _log.Log($"Quick connect: {protocol} {host}:{port}");
            QuickConnectHost = string.Empty;
        }
        catch (Exception ex)
        {
            _log.Log($"Quick connect failed: {ex.Message}", LogLevel.Error);
        }
    }

    private async Task OnOpenQuickConnectDialog()
    {
        var owner = GetMainWindow();
        if (owner is null) return;
        var dialog = new QuickConnectDialog();
        var result = await dialog.ShowDialog<QuickConnectResult?>(owner);

        if (result is not null)
        {
            QuickConnectProtocol = result.Protocol;
            await QuickConnectAsync(result.Hostname, result.Protocol, result.Username, result.Password);
        }
    }

    // ── Tools / Help ──────────────────────────────────────────────────────

    private async Task OnAbout()
    {
        var owner = GetMainWindow();
        if (owner is null) return;
        var dialog = new AboutDialog();
        await dialog.ShowDialog(owner);
    }

    private async Task OnPortScanner()
    {
        var owner = GetMainWindow();
        if (owner is null) return;
        var dialog = new PortScannerDialog();
        await dialog.ShowDialog(owner);
    }

    private Task OnOpenSftp()
    {
        var owner = GetMainWindow();
        if (owner is null) return Task.CompletedTask;

        // Pre-fill (and connect straight away) when an SSH connection is selected.
        ConnectionParameters? prefill = null;
        if (ConnectionTree.SelectedNode is { IsFolder: false, Model: { } model }
            && ConnectionParametersFactory.MapProtocol(model.Protocol) is ProtocolType.Ssh or ProtocolType.SshSftp)
        {
            prefill = ConnectionParametersFactory.FromConnectionInfo(model);
        }

        SshFileTransferDialog.ShowFor(owner, prefill);
        return Task.CompletedTask;
    }

    private void OnResetLayout()
    {
        IsConnectionTreeVisible = true;
        IsLogPanelVisible = true;
        IsFullScreen = false;
        LayoutResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private async Task OpenUrlAsync(string url)
    {
        var launcher = GetMainWindow()?.Launcher;
        var opened = launcher is not null && await launcher.LaunchUriAsync(new Uri(url));
        if (opened)
            _log.Log($"Opened {url} in the browser.");
        else
            _log.Log($"Could not open a browser. Visit {url}", LogLevel.Warning);
    }

    private static Window? GetMainWindow() =>
        (global::Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
