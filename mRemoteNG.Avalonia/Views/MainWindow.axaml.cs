using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Protocols.Ssh;
using System.Reactive.Linq;

namespace mRemoteNG.Avalonia.Views;

public partial class MainWindow : Window
{
    private TerminalView? _localTerminal;
    private System.Diagnostics.Process? _localShellProcess;
    private DebugConsoleTraceListener? _traceListener;
    private NotifyCollectionChangedEventHandler? _sessionsHandler;
    private EventHandler<SelectionChangedEventArgs>? _bottomTabsHandler;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Enter key on quick connect textbox triggers connect
        QuickConnectHostBox.KeyDown += OnQuickConnectKeyDown;

        Closed += OnWindowClosed;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
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
