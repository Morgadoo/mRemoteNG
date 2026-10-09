using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Protocols.Ssh;
using ReactiveUI;
using System.Reactive.Linq;

namespace mRemoteNG.Avalonia.Views;

public partial class MainWindow : Window
{
    private static readonly GridLength DefaultTreeWidth = new(280);
    private static readonly GridLength DefaultBottomHeight = new(160);

    private TerminalView? _localTerminal;
    private System.Diagnostics.Process? _localShellProcess;
    private DebugConsoleTraceListener? _traceListener;
    private NotifyCollectionChangedEventHandler? _sessionsHandler;
    private EventHandler<SelectionChangedEventArgs>? _bottomTabsHandler;
    private MainWindowViewModel? _boundViewModel;
    private readonly List<IDisposable> _vmSubscriptions = [];
    private GridLength _treeWidth = DefaultTreeWidth;
    private GridLength _bottomHeight = DefaultBottomHeight;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    private bool _closeConfirmed;
    private bool _startupFileOpened;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Enter key on quick connect textbox triggers connect
        QuickConnectHostBox.KeyDown += OnQuickConnectKeyDown;
        SearchBox.KeyDown += OnSearchKeyDown;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        if (_startupFileOpened || DataContext is not MainWindowViewModel vm) return;
        _startupFileOpened = true;
        await vm.OpenStartupFileAsync(Program.StartupFilePath);
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || DataContext is not MainWindowViewModel { ConnectionTree.IsDirty: true } vm)
            return;

        // Ask first; close again once the user saved or discarded.
        e.Cancel = true;
        if (await vm.ConfirmDiscardOrSaveAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            BindLayoutAndInteractions(vm);

            // Unsubscribe previous handlers to prevent leaks on DataContext change
            if (_sessionsHandler is not null)
                vm.Sessions.Sessions.CollectionChanged -= _sessionsHandler;

            _sessionsHandler = (_, _) =>
                global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    UpdateSessionPanelVisibility(vm));
            vm.Sessions.Sessions.CollectionChanged += _sessionsHandler;
            UpdateSessionPanelVisibility(vm);

            // Wire Trace output to Debug Console (remove previous listener)
            if (_traceListener is not null)
                Trace.Listeners.Remove(_traceListener);
            _traceListener = new DebugConsoleTraceListener(vm.DebugConsole);
            Trace.Listeners.Add(_traceListener);

            // Initialize local terminal when the tab is first selected
            if (_bottomTabsHandler is null)
            {
                _bottomTabsHandler = (_, _) =>
                {
                    if (BottomTabs.SelectedIndex == 1 && _localTerminal is null)
                        InitializeLocalTerminal();
                };
                BottomTabs.SelectionChanged += _bottomTabsHandler;
            }
        }
    }

    /// <summary>Hooks the view-only concerns of the view model: panel layout, full screen and dialogs.</summary>
    private void BindLayoutAndInteractions(MainWindowViewModel vm)
    {
        if (ReferenceEquals(_boundViewModel, vm)) return;
        foreach (var subscription in _vmSubscriptions)
            subscription.Dispose();
        _vmSubscriptions.Clear();
        if (_boundViewModel is not null)
            _boundViewModel.LayoutResetRequested -= OnLayoutResetRequested;
        _boundViewModel = vm;

        _vmSubscriptions.Add(vm.WhenAnyValue(x => x.IsConnectionTreeVisible).Subscribe(SetTreeVisible));
        _vmSubscriptions.Add(vm.WhenAnyValue(x => x.IsLogPanelVisible).Subscribe(SetBottomPanelVisible));
        _vmSubscriptions.Add(vm.WhenAnyValue(x => x.IsFullScreen).Subscribe(SetFullScreen));
        vm.LayoutResetRequested += OnLayoutResetRequested;

        _vmSubscriptions.Add(vm.ConnectionTree.EditNode.RegisterHandler(async context =>
        {
            var dialog = new ConnectionDialog(context.Input);
            context.SetOutput(await dialog.ShowDialog<bool>(this));
        }));
        _vmSubscriptions.Add(vm.ConnectionTree.Confirm.RegisterHandler(async context =>
        {
            var (title, message) = context.Input;
            context.SetOutput(await MessageDialog.ConfirmAsync(this, title, message, "Delete", "Cancel"));
        }));
    }

    private void SetTreeVisible(bool visible)
    {
        var column = MainGrid.ColumnDefinitions[0];
        if (!visible && column.Width.Value > 0)
            _treeWidth = column.Width; // remember a splitter-resized width
        column.Width = visible ? _treeWidth : new GridLength(0);
        MainGrid.ColumnDefinitions[1].Width = visible ? new GridLength(4) : new GridLength(0);
        TreePanel.IsVisible = visible;
        TreeSplitter.IsVisible = visible;
    }

    private void SetBottomPanelVisible(bool visible)
    {
        var row = MainGrid.RowDefinitions[2];
        if (!visible && row.Height.Value > 0)
            _bottomHeight = row.Height;
        row.Height = visible ? _bottomHeight : new GridLength(0);
        MainGrid.RowDefinitions[1].Height = visible ? new GridLength(4) : new GridLength(0);
        BottomPanel.IsVisible = visible;
        BottomSplitter.IsVisible = visible;
    }

    private void SetFullScreen(bool fullScreen)
    {
        if (fullScreen && WindowState != WindowState.FullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
        else if (!fullScreen && WindowState == WindowState.FullScreen)
        {
            WindowState = _stateBeforeFullScreen;
        }
    }

    protected override void OnPropertyChanged(global::Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Keep the menu check mark right when the window manager leaves full screen.
        if (change.Property == WindowStateProperty && DataContext is MainWindowViewModel vm)
            vm.IsFullScreen = WindowState == WindowState.FullScreen;
    }

    private void OnLayoutResetRequested(object? sender, EventArgs e)
    {
        _treeWidth = DefaultTreeWidth;
        _bottomHeight = DefaultBottomHeight;
        SetTreeVisible(true);
        SetBottomPanelVisible(true);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && DataContext is MainWindowViewModel vm)
        {
            vm.IsConnectionTreeVisible = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel vm)
        {
            vm.ConnectionTree.SearchFilter = string.Empty;
            e.Handled = true;
        }
    }

    private void InitializeLocalTerminal()
    {
        _localTerminal = new TerminalView();
        LocalTerminalHost.Child = _localTerminal;
        _localTerminal.Focus();

        // Determine shell: PowerShell on Windows, bash/sh on Unix
        string shell;
        string shellArgs;
        if (OperatingSystem.IsWindows())
        {
            shell = "powershell.exe";
            shellArgs = "-NoLogo -NoProfile";
        }
        else if (File.Exists("/bin/bash"))
        {
            shell = "/bin/bash";
            shellArgs = "--login";
        }
        else
        {
            shell = "/bin/sh";
            shellArgs = "";
        }

        var psi = new ProcessStartInfo
        {
            FileName = shell,
            Arguments = shellArgs,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Environment = { ["TERM"] = "xterm-256color" },
        };

        _localShellProcess = new System.Diagnostics.Process { StartInfo = psi };
        _localShellProcess.Start();

        // Read stdout -> terminal (break on EOF to avoid spin loop)
        _ = Task.Run(async () =>
        {
            var buffer = new char[4096];
            while (!_localShellProcess.HasExited)
            {
                int read = await _localShellProcess.StandardOutput.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0) break;
                _localTerminal.Write(new string(buffer, 0, read));
            }
        });

        // Read stderr -> terminal (break on EOF to avoid spin loop)
        _ = Task.Run(async () =>
        {
            var buffer = new char[4096];
            while (!_localShellProcess.HasExited)
            {
                int read = await _localShellProcess.StandardError.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0) break;
                _localTerminal.Write(new string(buffer, 0, read));
            }
        });

        // Terminal keystrokes -> stdin
        _localTerminal.DataToSend += (_, data) =>
        {
            if (_localShellProcess is { HasExited: false })
            {
                _localShellProcess.StandardInput.Write(
                    System.Text.Encoding.UTF8.GetString(data));
                _localShellProcess.StandardInput.Flush();
            }
        };
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        // Clean up trace listener
        if (_traceListener is not null)
            Trace.Listeners.Remove(_traceListener);

        // Clean up local shell process
        if (_localShellProcess is { HasExited: false })
        {
            try { _localShellProcess.Kill(); } catch { }
        }
    }

    private void UpdateSessionPanelVisibility(MainWindowViewModel vm)
    {
        var hasSessions = vm.Sessions.Sessions.Count > 0;
        EmptySessionsPanel.IsVisible = !hasSessions;
        ActiveSessionsPanel.IsVisible = hasSessions;
    }

    private void OnQuickConnectKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainWindowViewModel vm)
        {
            vm.QuickConnectCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }

    private async void OnCopyLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var text = string.Join(Environment.NewLine,
            vm.LogPanel.Entries.Select(entry => $"{entry.FormattedTime} [{entry.Level}] {entry.Message}"));
        if (Clipboard is not null)
            await Clipboard.SetTextAsync(text);
    }

    private void OnClearLog(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.LogPanel.Clear();
    }

    private async void OnCopyDebug(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var text = string.Join(Environment.NewLine,
            vm.DebugConsole.Entries.Select(entry => $"{entry.FormattedTime} [{entry.Level}] {entry.Message}"));
        if (Clipboard is not null)
            await Clipboard.SetTextAsync(text);
    }

    private void OnClearDebug(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.DebugConsole.Clear();
    }
}

/// <summary>Bridges System.Diagnostics.Trace to the Debug Console panel.</summary>
internal sealed class DebugConsoleTraceListener(DebugConsoleDockable console) : TraceListener
{
    public override void Write(string? message)
    {
        if (!string.IsNullOrEmpty(message))
            console.Log(message);
    }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrEmpty(message))
            console.Log(message);
    }
}
