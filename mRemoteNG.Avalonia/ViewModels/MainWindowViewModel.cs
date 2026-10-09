using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Serializers.Xml;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Localization;
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

/// <summary>The tabs of the main window's bottom panel.</summary>
public enum BottomPanelTab
{
    Log,
    Terminal,
    Debug,
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
    private bool _isMultiSshToolbarVisible;
    private bool _isBottomPanelExpanded = true;
    private BottomPanelTab _bottomTab = BottomPanelTab.Log;
    private int _unseenLogProblems;
    private bool _unseenLogHasErrors;
    private IReadOnlyList<ConnectionInfo> _homeCards = [];
    private readonly ToastService? _toasts;
    private readonly AppSettingsService? _settings;

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
            this.RaisePropertyChanged(nameof(HasSessions));
            this.RaisePropertyChanged(nameof(SessionCountText));
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

    /// <summary>View → Multi-SSH toolbar.</summary>
    public bool IsMultiSshToolbarVisible
    {
        get => _isMultiSshToolbarVisible;
        set => this.RaiseAndSetIfChanged(ref _isMultiSshToolbarVisible, value);
    }

    /// <summary>The bottom panel shows its content (true) or only its header strip (Ctrl+J).</summary>
    public bool IsBottomPanelExpanded
    {
        get => _isBottomPanelExpanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _isBottomPanelExpanded, value);
            UpdateLogSeen();
        }
    }

    /// <summary>The bottom panel's selected tab.</summary>
    public BottomPanelTab BottomTab
    {
        get => _bottomTab;
        set
        {
            if (_bottomTab == value) return;
            this.RaiseAndSetIfChanged(ref _bottomTab, value);
            this.RaisePropertyChanged(nameof(IsLogTabSelected));
            this.RaisePropertyChanged(nameof(IsTerminalTabSelected));
            this.RaisePropertyChanged(nameof(IsDebugTabSelected));
            UpdateLogSeen();
        }
    }

    public bool IsLogTabSelected
    {
        get => _bottomTab == BottomPanelTab.Log;
        set => SelectTab(BottomPanelTab.Log, value);
    }

    public bool IsTerminalTabSelected
    {
        get => _bottomTab == BottomPanelTab.Terminal;
        set => SelectTab(BottomPanelTab.Terminal, value);
    }

    public bool IsDebugTabSelected
    {
        get => _bottomTab == BottomPanelTab.Debug;
        set => SelectTab(BottomPanelTab.Debug, value);
    }

    /// <summary>Warnings and errors logged since the log was last on screen (status bar indicator).</summary>
    public int UnseenLogProblems
    {
        get => _unseenLogProblems;
        private set
        {
            this.RaiseAndSetIfChanged(ref _unseenLogProblems, value);
            this.RaisePropertyChanged(nameof(HasUnseenLogProblems));
        }
    }

    public bool HasUnseenLogProblems => _unseenLogProblems > 0;

    /// <summary>At least one of the unseen problems is an error (the indicator turns red).</summary>
    public bool UnseenLogHasErrors
    {
        get => _unseenLogHasErrors;
        private set => this.RaiseAndSetIfChanged(ref _unseenLogHasErrors, value);
    }

    /// <summary>The command palette (Ctrl+K / Ctrl+Shift+P).</summary>
    public CommandPaletteViewModel Palette { get; }

    /// <summary>Connections opened most recently (persisted in the settings).</summary>
    public RecentConnections Recent { get; } = new();

    /// <summary>Cards of the empty session area: recent connections, then favourites (at most <see cref="MaxHomeCards"/>).</summary>
    public IReadOnlyList<ConnectionInfo> HomeCards
    {
        get => _homeCards;
        private set
        {
            this.RaiseAndSetIfChanged(ref _homeCards, value);
            this.RaisePropertyChanged(nameof(HasHomeCards));
        }
    }

    public bool HasHomeCards => _homeCards.Count > 0;

    public const int MaxHomeCards = 8;

    /// <summary>Raised by the empty state's "Quick connect": the view focuses the header's address field.</summary>
    public event EventHandler? QuickConnectFocusRequested;

    /// <summary>Raised by Ctrl+F: the view focuses the tree's search box.</summary>
    public event EventHandler? FindConnectionRequested;

    /// <summary>The Multi-SSH toolbar (types into every open terminal session).</summary>
    public MultiSshViewModel MultiSsh { get; }

    /// <summary>Raised by View → Reset Layout; the view restores panel sizes.</summary>
    public event EventHandler? LayoutResetRequested;

    /// <summary>Status-bar text describing the open connection file (full path; the tooltip of <see cref="FileStatusName"/>).</summary>
    public string FileStatus => ConnectionTree.DatabaseName is { } database
        ? Localizer.Format("SqlDatabaseStatusFormat", database)
        : ConnectionTree.CurrentFilePath ?? Localizer.Get("NewConnectionFileNotSaved");

    /// <summary>Status bar: the file name only, or the SQL database name.</summary>
    public string FileStatusName => ConnectionTree.DatabaseName
                                    ?? (ConnectionTree.CurrentFilePath is { } path ? System.IO.Path.GetFileName(path) : Localizer.Get("Untitled"));

    /// <summary>The connections come from an SQL database (the status bar shows a database icon).</summary>
    public bool IsSqlSource => ConnectionTree.DatabaseName is not null;

    public bool HasSessions => ActiveConnectionCount > 0;

    /// <summary>Status bar: "1 session" / "3 sessions".</summary>
    public string SessionCountText => ActiveConnectionCount == 1
        ? Localizer.Get("ShellOneSession")
        : Localizer.Format("ShellSessionsFormat", ActiveConnectionCount);

    /// <summary>Status bar: the theme in use ("Dark", "Light", "Darcula"…).</summary>
    public string ThemeDisplayName
    {
        get
        {
            var themes = ThemeService.Instance;
            if (!string.IsNullOrEmpty(themes.CurrentThemeName))
                return themes.CurrentThemeName;
            var effective = IsDarkTheme ? Localizer.Get("ShellThemeDark") : Localizer.Get("ShellThemeLight");
            return themes.CurrentTheme == ThemeMode.System ? Localizer.Format("ShellThemeSystemFormat", effective) : effective;
        }
    }

    /// <summary>The theme shown is dark (the header's theme button offers light).</summary>
    public bool IsDarkTheme => ThemeService.Instance.EffectiveVariant != global::Avalonia.Styling.ThemeVariant.Light;

    /// <summary>Tooltip of the header's theme button.</summary>
    public string ThemeToggleTip => Localizer.Get(IsDarkTheme ? "ShellSwitchToLightTheme" : "ShellSwitchToDarkTheme");

    /// <summary>Status bar: "v1.78.2-dev".</summary>
    public string VersionText { get; } = "v" + UpdateCheckServiceVersion();

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
    public ReactiveCommand<Unit, Unit> NextSessionCommand { get; }
    public ReactiveCommand<Unit, Unit> PreviousSessionCommand { get; }
    public ReactiveCommand<Unit, Unit> ReconnectAllCommand { get; }
    public ReactiveCommand<Unit, Unit> DisconnectAllCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseAllSessionsCommand { get; }
    public ReactiveCommand<Unit, Unit> NewPanelCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectSelectedToPanelCommand { get; }
    public ReactiveCommand<PanelArrangement, Unit> ArrangePanelsCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleMultiSshToolbarCommand { get; }
    public ReactiveCommand<Unit, Unit> ExternalToolsCommand { get; }
    public ReactiveCommand<Unit, Unit> UltraVncListenerCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenDatabaseCommand { get; }
    public ReactiveCommand<Unit, Unit> ReloadDatabaseCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenLogFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleBottomPanelCommand { get; }
    public ReactiveCommand<Unit, Unit> ShowLogCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleThemeCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCommandPaletteCommand { get; }
    public ReactiveCommand<Unit, Unit> FocusQuickConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> FindConnectionCommand { get; }
    public ReactiveCommand<ConnectionInfo, Unit> ConnectConnectionCommand { get; }

    public MainWindowViewModel(
        ConnectionTreeViewModel connectionTree,
        ConnectionsService connectionsService,
        SessionsDockable sessions,
        LogPanelDockable logPanel,
        DebugConsoleDockable debugConsole,
        ToastService? toasts = null,
        AppSettingsService? settings = null)
    {
        _toasts = toasts;
        _settings = settings;
        ConnectionTree = connectionTree;
        _connectionsService = connectionsService;
        _sessions = sessions;
        _log = logPanel;
        _debug = debugConsole;
        Sessions = sessions;
        LogPanel = logPanel;
        DebugConsole = debugConsole;
        MultiSsh = new MultiSshViewModel(sessions);

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

        // Sessions menu
        var hasSessions = Observable.FromEventPattern<System.Collections.Specialized.NotifyCollectionChangedEventHandler,
                System.Collections.Specialized.NotifyCollectionChangedEventArgs>(
                h => sessions.Sessions.CollectionChanged += h, h => sessions.Sessions.CollectionChanged -= h)
            .Select(_ => sessions.Sessions.Count > 0)
            .StartWith(sessions.Sessions.Count > 0);
        NextSessionCommand = ReactiveCommand.Create(() => _sessions.SelectAdjacentSession(+1), hasSessions);
        PreviousSessionCommand = ReactiveCommand.Create(() => _sessions.SelectAdjacentSession(-1), hasSessions);
        ReconnectAllCommand = ReactiveCommand.CreateFromTask(() => _sessions.ReconnectAllAsync(), hasSessions);
        DisconnectAllCommand = ReactiveCommand.CreateFromTask(() => _sessions.DisconnectAllAsync(), hasSessions);
        CloseAllSessionsCommand = ReactiveCommand.CreateFromTask(() => _sessions.CloseAllSessionsAsync(), hasSessions);
        NewPanelCommand = ReactiveCommand.CreateFromTask(OnNewPanelAsync);
        ConnectSelectedToPanelCommand = ReactiveCommand.CreateFromTask(OnConnectSelectedToPanelAsync,
            ConnectionTree.WhenAnyValue(t => t.SelectedNode).Select(n => n is { IsFolder: false }));
        ArrangePanelsCommand = ReactiveCommand.Create<PanelArrangement>(mode => _sessions.Arrangement = mode);
        ToggleMultiSshToolbarCommand = ReactiveCommand.Create(() => { IsMultiSshToolbarVisible = !IsMultiSshToolbarVisible; });

        // Tools / storage
        ExternalToolsCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (GetMainWindow() is { } owner)
                await Views.ExternalToolsWindow.ShowAsync(owner, ConnectionTree.SelectedNode?.Model);
        });
        UltraVncListenerCommand = ReactiveCommand.Create(() => { Views.Dialogs.UltraVncListenerWindow.ShowOrActivate(GetMainWindow()); });
        OpenDatabaseCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (GetMainWindow() is { } owner && await ConfirmDiscardOrSaveAsync())
                await AppServices.GetRequired<StorageRuntime>().OpenDatabaseAsync(owner);
        });
        ReloadDatabaseCommand = ReactiveCommand.Create(
            () => { AppServices.GetRequired<StorageRuntime>().ReloadDatabase(); },
            ConnectionTree.WhenAnyValue(t => t.DatabaseName).Select(name => name is not null));
        OpenLogFileCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            var path = AppServices.GetRequired<StorageRuntime>().LogFilePath;
            if (!await FileLauncher.OpenFileAsync(GetMainWindow(), path))
                _log.Log($"Could not open the log file {path}", LogLevel.Warning);
        });

        // Shell: bottom panel, theme, palette, empty state
        ToggleBottomPanelCommand = ReactiveCommand.Create(ToggleBottomPanel);
        ShowLogCommand = ReactiveCommand.Create(ShowLog);
        ToggleThemeCommand = ReactiveCommand.Create(ToggleTheme);
        Palette = new CommandPaletteViewModel(AllConnections, () => Recent.Resolve(ConnectionTree.Root))
        {
            Connect = ConnectFromPaletteAsync,
        };
        OpenCommandPaletteCommand = ReactiveCommand.Create(() => Palette.Toggle());
        FocusQuickConnectCommand = ReactiveCommand.Create(() => QuickConnectFocusRequested?.Invoke(this, EventArgs.Empty));
        FindConnectionCommand = ReactiveCommand.Create(() =>
        {
            IsConnectionTreeVisible = true;
            FindConnectionRequested?.Invoke(this, EventArgs.Empty);
        });
        ConnectConnectionCommand = ReactiveCommand.CreateFromTask<ConnectionInfo>(connection => ConnectionTree.ConnectAsync(connection, null));

        Recent.Load(_settings?.Current.RecentConnections);
        Recent.Changed += (_, _) =>
        {
            _settings?.Update(s => s.RecentConnections = Recent.Serialize());
            RefreshHomeCards();
        };
        ConnectionTree.WhenAnyValue(t => t.CurrentFilePath, t => t.DatabaseName, t => t.IsDirty)
            .Subscribe(_ => RefreshHomeCards());

        // The status bar's warning/error count, cleared while the log is on screen.
        logPanel.Entries.CollectionChanged += OnLogEntriesChanged;
        this.WhenAnyValue(x => x.IsLogPanelVisible).Subscribe(_ => UpdateLogSeen());
        _toasts?.AttachLog(logPanel, ShowLog);

        ThemeService.Instance.WhenAnyValue(t => t.CurrentThemeName, t => t.CurrentTheme, t => t.EffectiveVariant)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(ThemeDisplayName));
                this.RaisePropertyChanged(nameof(IsDarkTheme));
                this.RaisePropertyChanged(nameof(ThemeToggleTip));
            });

        // Track the open session count and the most recently opened connections
        sessions.Sessions.CollectionChanged += (_, e) =>
        {
            ActiveConnectionCount = sessions.Sessions.Count;
            foreach (SessionTabViewModel session in e.NewItems ?? Array.Empty<SessionTabViewModel>())
            {
                if (session.Connection is { } connection)
                    Recent.Add(connection);
            }
        };

        // Title and status bar follow the file name and unsaved-changes state.
        ConnectionTree.WhenAnyValue(t => t.IsDirty, t => t.CurrentFilePath, t => t.DatabaseName)
            .Subscribe(_ =>
            {
                UpdateTitle();
                this.RaisePropertyChanged(nameof(FileStatus));
                this.RaisePropertyChanged(nameof(FileStatusName));
                this.RaisePropertyChanged(nameof(IsSqlSource));
            });

        // Route any unhandled command errors to the log panel
        foreach (var command in new IHandleObservableErrors[]
                 {
                     NewFileCommand, NewConnectionCommand, OpenConnectionFileCommand, SaveConnectionFileCommand,
                     SaveAsConnectionFileCommand, ImportCommand, ExportCommand, OpenOptionsCommand,
                     QuickConnectCommand, OpenQuickConnectDialogCommand, AboutCommand, PortScannerCommand,
                     OpenSftpCommand, OpenGitHubCommand, OpenDocumentationCommand, ReportBugCommand,
                     CheckForUpdatesCommand, ReconnectAllCommand, DisconnectAllCommand, CloseAllSessionsCommand,
                     ConnectSelectedToPanelCommand, ExternalToolsCommand, UltraVncListenerCommand,
                     OpenDatabaseCommand, ReloadDatabaseCommand, OpenLogFileCommand, NewPanelCommand,
                     ConnectConnectionCommand,
                 })
        {
            command.ThrownExceptions.Subscribe(ex => _log.Log($"Error: {ex.Message}", LogLevel.Error));
        }
    }

    private void UpdateTitle()
    {
        var path = ConnectionTree.CurrentFilePath;
        var document = ConnectionTree.DatabaseName
                       ?? (path is null ? Localizer.Get("Untitled") : System.IO.Path.GetFileName(path));
        var title = $"mRemoteNG — {document}{(ConnectionTree.IsDirty ? "*" : "")}";
        if (ActiveConnectionCount > 0)
            title += " " + Localizer.Format(ActiveConnectionCount == 1 ? "TitleOneActiveConnection" : "TitleActiveConnectionsFormat",
                ActiveConnectionCount);
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
                Title = Localizer.Get("OpenConnectionFile", "Open Connection File"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new("mRemoteNG XML") { Patterns = ["*.xml"] },
                    new(Localizer.Get("FilterAll", "All files")) { Patterns = ["*.*"] },
                ],
            });
        if (files.Count > 0)
            await LoadConnectionFileAsync(window, files[0].Path.LocalPath);
    }

    /// <summary>Opens the connection file given on the command line (if any) once the window is shown.</summary>
    public async Task OpenStartupFileAsync(string? path)
    {
        CreateStartupPanel();
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
                OpenPreviousSessions();
                return;
            }
            catch (ConnectionFilePasswordException ex)
            {
                if (ex.PasswordWasSupplied)
                    error = Localizer.Get("IncorrectPasswordTryAgain");

                var prompt = new PasswordPromptDialog(
                    Localizer.Format("FileIsPasswordProtectedFormat", System.IO.Path.GetFileName(path)), error);
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
        if (ConnectionTree.DatabaseName is { } database)
        {
            try
            {
                ConnectionTree.SaveToFile();
                _log.Log($"Saved connections to SQL database {database}");
                ToastSaved(database);
                return true;
            }
            catch (Exception ex)
            {
                _log.Log($"Failed to save connections to the SQL database: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        if (ConnectionTree.CurrentFilePath is null)
            return await SaveAsAsync();

        try
        {
            ConnectionTree.SaveToFile();
            _log.Log($"Saved connection file: {ConnectionTree.CurrentFilePath}");
            RememberOpenFile();
            ToastSaved(System.IO.Path.GetFileName(ConnectionTree.CurrentFilePath));
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
                Title = Localizer.Get("SaveConnectionFile"),
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
            ToastSaved(System.IO.Path.GetFileName(file.Path.LocalPath));
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
            if (await MessageDialog.ConfirmAsync(window, Localizer.Get("UpdateAvailableTitle"),
                    $"{result.Message}\n\n{Localizer.Get("OpenReleasePageQuestion")}", Localizer.Get("Open"), Localizer.Get("Later")))
                await OpenUrlAsync(url);
        }
        else
        {
            await new MessageDialog(Localizer.Get("MenuItem_CheckForUpdates"), result.Message,
                new MessageDialogButton(Localizer.Get("_Ok"), "ok", IsDefault: true, IsCancel: true)).ShowDialog<string?>(window);
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

        var question = ConnectionTree.CurrentFilePath is { } path
            ? Localizer.Format("SaveChangesToFormat", System.IO.Path.GetFileName(path))
            : Localizer.Get("SaveChangesToNewFile");
        var dialog = new MessageDialog(Localizer.Get("UnsavedChangesTitle"), question,
            new MessageDialogButton(Localizer.Get("_Cancel"), nameof(UnsavedChangesChoice.Cancel), IsCancel: true),
            new MessageDialogButton(Localizer.Get("DontSave"), nameof(UnsavedChangesChoice.Discard)),
            new MessageDialogButton(Localizer.Get("Save"), nameof(UnsavedChangesChoice.Save), IsDefault: true));
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
        // Sources that are not files (Active Directory) bring their password from the import dialog.
        string? password = request.Password;

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
                    Localizer.Format("FileIsPasswordProtectedFormat", fileName),
                    ex.PasswordWasSupplied ? Localizer.Get("IncorrectPasswordTryAgain") : null);
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
        var (host, protocol, username) = ParseQuickConnectInput(QuickConnectHost, QuickConnectProtocol);
        QuickConnectProtocol = protocol;
        await QuickConnectAsync(host, protocol, username, null);
    }

    private static readonly Dictionary<string, CoreProtocolType> QuickConnectSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ssh"] = CoreProtocolType.SSH2,
        ["rdp"] = CoreProtocolType.RDP,
        ["vnc"] = CoreProtocolType.VNC,
        ["telnet"] = CoreProtocolType.Telnet,
        ["rlogin"] = CoreProtocolType.Rlogin,
        ["raw"] = CoreProtocolType.RAW,
        ["http"] = CoreProtocolType.HTTP,
        ["https"] = CoreProtocolType.HTTPS,
    };

    /// <summary>
    /// Reads the header's quick connect field: "host[:port]" with the selected protocol, a scheme that picks the
    /// protocol ("ssh://host", "rdp://host:3390"; http/https URLs stay URLs) and an optional "user@" prefix.
    /// </summary>
    public static (string Host, CoreProtocolType Protocol, string? Username) ParseQuickConnectInput(string input, CoreProtocolType selected)
    {
        var text = input.Trim();
        var protocol = selected;
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme > 0 && QuickConnectSchemes.TryGetValue(text[..scheme], out var fromScheme))
        {
            protocol = fromScheme;
            if (protocol is CoreProtocolType.HTTP or CoreProtocolType.HTTPS)
                return (text, protocol, null);
            text = text[(scheme + 3)..].TrimEnd('/');
        }

        string? username = null;
        var at = text.LastIndexOf('@');
        if (at > 0 && protocol is not (CoreProtocolType.HTTP or CoreProtocolType.HTTPS))
        {
            username = text[..at];
            text = text[(at + 1)..];
        }
        return (text, protocol, username);
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
        IsMultiSshToolbarVisible = false;
        _sessions.ResetLayout();
        LayoutResetRequested?.Invoke(this, EventArgs.Empty);
    }

    // ── Sessions / panels / favorites ─────────────────────────────────────

    /// <summary>
    /// "Reconnect to previously opened sessions on startup": after a file loads, opens every connection
    /// saved with Connected="true" that is not open yet. Returns the number of sessions started.
    /// </summary>
    public int OpenPreviousSessions()
    {
        var settings = AppServices.GetRequired<AppSettingsService>().Current;
        if (!settings.OpenConnectionsFromLastSession || ConnectionTree.Root is not { } root
            || AppServices.Provider.GetService(typeof(mRemoteNG.Core.App.StartupArguments)) is mRemoteNG.Core.App.StartupArguments { NoReconnect: true })
            return 0;
        var count = _sessions.OpenPreviousSessions(root, AppServices.GetRequired<IProtocolFactory>());
        if (count > 0)
            _log.Log($"Reopening {count} session(s) from the last session.");
        return count;
    }

    /// <summary>Options ▸ Tabs &amp; Panels ▸ "Create an empty panel when mRemoteNG starts".</summary>
    public void CreateStartupPanel()
    {
        var settings = AppServices.GetRequired<AppSettingsService>().Current;
        if (!settings.CreateEmptyPanelOnStartUp) return;
        var name = string.IsNullOrWhiteSpace(settings.StartUpPanelName) ? SessionsDockable.NewPanelBaseName : settings.StartUpPanelName;
        _sessions.GetOrCreatePanel(name);
    }

    /// <summary>Sessions ▸ Connect to Panel…: asks for a panel and opens the selected connection in it.</summary>
    private async Task OnConnectSelectedToPanelAsync()
    {
        if (ConnectionTree.SelectedNode is not { IsFolder: false, Model: { } model }) return;
        var suggestion = string.IsNullOrWhiteSpace(model.Panel) ? SessionsDockable.DefaultPanelName : model.Panel;
        var panel = _sessions.PanelChooser is { } choose ? await choose(_sessions.PanelNames, suggestion) : suggestion;
        if (panel is null) return;
        await _sessions.OpenConnectionAsync(model, AppServices.GetRequired<IProtocolFactory>(), new ConnectOptions { Panel = panel });
    }

    /// <summary>Connections of the loaded file marked as favorites, in tree order.</summary>
    public IReadOnlyList<ConnectionInfo> GetFavorites() =>
        ConnectionTree.Root is { } root
            ? root.GetRecursiveChildList()
                .Where(c => c is not global::mRemoteNG.Core.Container.ContainerInfo && c.Favorite)
                .ToList()
            : [];

    /// <summary>Favorites ▸ connection: opens a session for it.</summary>
    public async Task ConnectFavoriteAsync(ConnectionInfo connection)
    {
        try
        {
            await _sessions.OpenConnectionAsync(connection, AppServices.GetRequired<IProtocolFactory>());
        }
        catch (Exception ex)
        {
            _log.Log($"Could not connect to \"{connection.Name}\": {ex.Message}", LogLevel.Error);
        }
    }

    // ── Shell: bottom panel, log indicator, theme, palette, empty state ──

    private void SelectTab(BottomPanelTab tab, bool selected)
    {
        if (selected)
            BottomTab = tab;
        else
            RaiseTabSelection(); // a tab button cannot be unchecked: re-check the current one
    }

    private void RaiseTabSelection()
    {
        this.RaisePropertyChanged(nameof(IsLogTabSelected));
        this.RaisePropertyChanged(nameof(IsTerminalTabSelected));
        this.RaisePropertyChanged(nameof(IsDebugTabSelected));
    }

    /// <summary>Ctrl+J: collapses the bottom panel to its header or expands it (showing it when hidden).</summary>
    private void ToggleBottomPanel()
    {
        if (!IsLogPanelVisible)
        {
            IsLogPanelVisible = true;
            IsBottomPanelExpanded = true;
            return;
        }
        IsBottomPanelExpanded = !IsBottomPanelExpanded;
    }

    /// <summary>Shows the log tab of the bottom panel (status bar indicator, toasts' "Show log").</summary>
    public void ShowLog()
    {
        IsLogPanelVisible = true;
        IsBottomPanelExpanded = true;
        BottomTab = BottomPanelTab.Log;
        UpdateLogSeen();
    }

    private bool IsLogOnScreen => IsLogPanelVisible && IsBottomPanelExpanded && BottomTab == BottomPanelTab.Log;

    private void UpdateLogSeen()
    {
        if (!IsLogOnScreen) return;
        UnseenLogProblems = 0;
        UnseenLogHasErrors = false;
    }

    private void OnLogEntriesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            UnseenLogProblems = 0;
            UnseenLogHasErrors = false;
            return;
        }
        if (e.NewItems is null || IsLogOnScreen) return;
        foreach (LogEntry entry in e.NewItems)
        {
            if (entry.Level is LogLevel.Warning or LogLevel.Error)
                UnseenLogProblems++;
            if (entry.Level == LogLevel.Error)
                UnseenLogHasErrors = true;
        }
    }

    /// <summary>The header's theme button: switches between the dark and light theme and saves the choice.</summary>
    private void ToggleTheme()
    {
        var target = IsDarkTheme ? ThemeMode.Light : ThemeMode.Dark;
        if (_settings is not null)
        {
            _settings.Update(s =>
            {
                s.Theme = target;
                s.ThemeName = string.Empty;
            });
            ThemeService.Instance.ApplySettings(_settings.Current);
        }
        else
        {
            ThemeService.Instance.Apply(target);
        }
    }

    private void ToastSaved(string? name)
    {
        if (!string.IsNullOrEmpty(name))
            _toasts?.Show(Localizer.Format("ShellSavedFormat", name), level: ToastLevel.Success);
    }

    /// <summary>Every connection of the loaded tree (no folders), in tree order.</summary>
    private IEnumerable<ConnectionInfo> AllConnections() =>
        ConnectionTree.Root is { } root
            ? root.GetRecursiveChildList().Where(c => c is not global::mRemoteNG.Core.Container.ContainerInfo)
            : [];

    /// <summary>Command palette: Enter connects, Ctrl+Enter opens "connect with options" for the connection.</summary>
    private async Task ConnectFromPaletteAsync(ConnectionInfo connection, bool withOptions)
    {
        if (!withOptions)
        {
            await ConnectionTree.ConnectAsync(connection, null);
            return;
        }
        if (FindNodeForModel(ConnectionTree.Nodes, connection) is not { } node)
            return;
        ConnectionTree.SelectedNode = node;
        if (await ConnectionTree.ConnectWithOptionsCommand.CanExecute.FirstAsync())
            await ConnectionTree.ConnectWithOptionsCommand.Execute();
    }

    /// <summary>Recomputes the empty state's cards: recent connections, then favourites.</summary>
    public void RefreshHomeCards()
    {
        var recent = Recent.Resolve(ConnectionTree.Root);
        var cards = recent.Concat(GetFavorites().Where(f => !recent.Contains(f))).Take(MaxHomeCards).ToList();
        if (!cards.SequenceEqual(_homeCards))
            HomeCards = cards;
    }

    /// <summary>
    /// Asks for a new panel's name given a suggestion (the main window shows a <see cref="TextPromptDialog"/>);
    /// null = cancelled. Without it the suggestion is used.
    /// </summary>
    public Func<string, Task<string?>>? PanelNamePrompt { get; set; }

    /// <summary>Sessions ▸ New Panel: asks for the name (like "Move to Panel ▸ New Panel…").</summary>
    private async Task OnNewPanelAsync()
    {
        var suggestion = _sessions.UniquePanelName(SessionsDockable.NewPanelBaseName);
        var name = PanelNamePrompt is { } prompt ? await prompt(suggestion) : suggestion;
        if (string.IsNullOrWhiteSpace(name)) return;
        var existing = _sessions.FindPanel(name.Trim());
        if (existing is not null)
            _sessions.ActivePanel = existing;
        else
            _sessions.NewPanel(name);
    }

    private static string UpdateCheckServiceVersion()
    {
        try
        {
            if (AppServices.Provider.GetService(typeof(UpdateCheckService)) is UpdateCheckService updates)
                return updates.CurrentVersionText;
        }
        catch (InvalidOperationException)
        {
            // No container (designer): the assembly version.
        }
        return typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? string.Empty;
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
