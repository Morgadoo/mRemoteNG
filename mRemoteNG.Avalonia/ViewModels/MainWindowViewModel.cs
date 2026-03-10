using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Avalonia.Views.Dialogs;
using ReactiveUI;
using System.Reactive;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the main application window.
/// Owns top-level navigation state, docking layout, and all menu commands.
/// </summary>
public sealed class MainWindowViewModel : ReactiveObject
{
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

    // ── Commands ──────────────────────────────────────────────────────────
    public ReactiveCommand<Unit, Unit> NewConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenConnectionFileCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveConnectionFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ImportCommand { get; }
    public ReactiveCommand<Unit, Unit> ExportCommand { get; }
    public ReactiveCommand<Unit, Unit> ExitCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenOptionsCommand { get; }
    public ReactiveCommand<Unit, Unit> QuickConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenQuickConnectDialogCommand { get; }
    public ReactiveCommand<Unit, Unit> AboutCommand { get; }
    public ReactiveCommand<Unit, Unit> PortScannerCommand { get; }

    public MainWindowViewModel(ConnectionTreeViewModel connectionTree)
    {
        ConnectionTree = connectionTree;

        NewConnectionCommand = ReactiveCommand.Create(OnNewConnection);
        OpenConnectionFileCommand = ReactiveCommand.CreateFromTask(OnOpenConnectionFile);
        SaveConnectionFileCommand = ReactiveCommand.Create(OnSaveConnectionFile);
        ImportCommand = ReactiveCommand.CreateFromTask(OnImport);
        ExportCommand = ReactiveCommand.CreateFromTask(OnExport);
        ExitCommand = ReactiveCommand.Create(() => System.Environment.Exit(0));
        OpenOptionsCommand = ReactiveCommand.CreateFromTask(OnOpenOptions);
        QuickConnectCommand = ReactiveCommand.Create(OnQuickConnect);
        OpenQuickConnectDialogCommand = ReactiveCommand.CreateFromTask(OnOpenQuickConnectDialog);
        AboutCommand = ReactiveCommand.CreateFromTask(OnAbout);
        PortScannerCommand = ReactiveCommand.Create(OnPortScanner);
    }

    private void OnNewConnection()
    {
        var dialog = new ConnectionDialog();
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
                    new("mRemoteNG CSV") { Patterns = ["*.csv"] },
                    new("All files") { Patterns = ["*.*"] },
                ],
            });
        if (files.Count > 0)
        {
            // Phase 3: load file into ConnectionTree via ConnectionsService
            System.Diagnostics.Trace.TraceInformation($"Open: {files[0].Path.LocalPath}");
        }
    }

    private void OnSaveConnectionFile()
    {
        // Phase 3: persist ConnectionTree via ConnectionsService
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

    private void OnQuickConnect()
    {
        if (string.IsNullOrWhiteSpace(QuickConnectHost)) return;
        // Phase 3: initiate connection via ProtocolFactory
        System.Diagnostics.Trace.TraceInformation($"Quick connect: {QuickConnectProtocol}://{QuickConnectHost}");
        QuickConnectHost = string.Empty;
    }

    private async Task OnOpenQuickConnectDialog()
    {
        var dialog = new QuickConnectDialog();
        await dialog.ShowDialog(GetMainWindow());
    }

    private async Task OnAbout()
    {
        var dialog = new AboutDialog();
        await dialog.ShowDialog(GetMainWindow());
    }

    private void OnPortScanner()
    {
        // Phase 3: open port scanner window
    }

    private static Window? GetMainWindow() =>
        (global::Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
