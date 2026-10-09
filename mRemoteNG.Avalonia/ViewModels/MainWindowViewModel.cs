using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Connection;
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
    public const string ReleasesUrl = "https://github.com/mRemoteNG/mRemoteNG/releases";

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
        CheckForUpdatesCommand = ReactiveCommand.CreateFromTask(() => OpenUrlAsync(ReleasesUrl));

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
        var dialog = new ImportDialog();
        await dialog.ShowDialog(GetMainWindow());
    }

    private async Task OnExport()
    {
        var dialog = new ExportDialog();
        await dialog.ShowDialog(GetMainWindow());
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
            var (host, port) = QuickConnectViewModel.ParseHost(hostInput, protocol);
            var info = ConnectionDefaults.ApplyNewConnectionDefaults(new ConnectionInfo());
            info.Name = host;
            info.IsQuickConnect = true;
            info.Protocol = protocol;
            info.Hostname = host;
            info.Port = port;
            info.Username = username ?? string.Empty;
            info.Password = password ?? string.Empty;

            var parameters = ConnectionParametersFactory.FromConnectionInfo(info);
            var factory = AppServices.GetRequired<IProtocolFactory>();
            await _sessions.OpenConnectionAsync(parameters, factory);

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

    private async Task OnOpenSftp()
    {
        var owner = GetMainWindow();
        if (owner is null) return;
        await new SshFileTransferDialog().ShowDialog(owner);
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
