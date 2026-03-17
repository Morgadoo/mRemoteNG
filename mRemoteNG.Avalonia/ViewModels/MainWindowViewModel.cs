using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Core.Config.Connections;
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
        {
            try
            {
                var path = files[0].Path.LocalPath;
                ConnectionTree.LoadFromFile(path);
                _log.Log($"Loaded connection file: {path}");
                Title = $"mRemoteNG — {System.IO.Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                _log.Log($"Failed to load connection file: {ex.Message}", LogLevel.Error);
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
