using ReactiveUI;
using System.Reactive;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>
/// ViewModel for the main application window.
/// Owns the top-level navigation state, docking layout, and menu commands.
/// </summary>
public sealed class MainWindowViewModel : ReactiveObject
{
    private string _title = "mRemoteNG";
    private bool _isConnected;
    private int _activeConnectionCount;

    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set => this.RaiseAndSetIfChanged(ref _isConnected, value);
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

    /// <summary>Child ViewModel for the connection tree panel.</summary>
    public ConnectionTreeViewModel ConnectionTree { get; }

    // --- Commands (wired to menu items and toolbar buttons) ---

    public ReactiveCommand<Unit, Unit> NewConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenConnectionFileCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveConnectionFileCommand { get; }
    public ReactiveCommand<Unit, Unit> ExitCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenOptionsCommand { get; }
    public ReactiveCommand<Unit, Unit> AboutCommand { get; }

    public MainWindowViewModel(ConnectionTreeViewModel connectionTree)
    {
        ConnectionTree = connectionTree;

        NewConnectionCommand = ReactiveCommand.Create(OnNewConnection);
        OpenConnectionFileCommand = ReactiveCommand.Create(OnOpenConnectionFile);
        SaveConnectionFileCommand = ReactiveCommand.Create(OnSaveConnectionFile);
        ExitCommand = ReactiveCommand.Create(OnExit);
        OpenOptionsCommand = ReactiveCommand.Create(OnOpenOptions);
        AboutCommand = ReactiveCommand.Create(OnAbout);
    }

    private void OnNewConnection() { /* Phase 2: open new connection dialog */ }
    private void OnOpenConnectionFile() { /* Phase 2: open file picker */ }
    private void OnSaveConnectionFile() { /* Phase 2: save current connections */ }
    private void OnExit() => System.Environment.Exit(0);
    private void OnOpenOptions() { /* Phase 2: open options dialog */ }
    private void OnAbout() { /* Phase 2: open about dialog */ }
}
