using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Avalonia.Views.Sessions;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Core.Settings;
using mRemoteNG.Protocols.Ssh;
using ReactiveUI;
using System.Reactive.Linq;

namespace mRemoteNG.Avalonia.Views;

public partial class MainWindow : Window
{
    private static readonly GridLength DefaultTreeWidth = new(WindowLayoutState.DefaultTreeWidth);
    private static readonly GridLength DefaultBottomHeight = new(WindowLayoutState.DefaultBottomHeight);

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
    private bool _closePromptOpen;
    private bool _startupFileOpened;
    private bool _layoutRestored;
    private PixelPoint? _normalPosition;
    private Size? _normalSize;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Enter key on quick connect textbox triggers connect
        QuickConnectHostBox.KeyDown += OnQuickConnectKeyDown;
        SearchBox.KeyDown += OnSearchKeyDown;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        MultiSshBox.AddHandler(KeyDownEvent, OnMultiSshKeyDown, RoutingStrategies.Tunnel);
        MultiSshBox.GotFocus += (_, _) => (DataContext as MainWindowViewModel)?.MultiSsh.RefreshTargets();
        SessionsMenu.SubmenuOpened += (_, _) => RebuildSessionList();
        FavoritesMenu.SubmenuOpened += (_, _) => RebuildFavorites();
        ViewMenu.SubmenuOpened += (_, _) => UpdateArrangementChecks();
        SessionArea.LayoutChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel current)
                UpdateSessionPanelVisibility(current);
        };
        PositionChanged += (_, _) => RememberNormalBounds();

        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        if (_startupFileOpened || DataContext is not MainWindowViewModel vm) return;
        _startupFileOpened = true;
        EnsureOnScreen();
        await vm.OpenStartupFileAsync(Program.StartupFilePath);
    }

    private async void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || DataContext is not MainWindowViewModel vm)
            return;

        // Closing can't be awaited: cancel, ask (unsaved changes, then open sessions), and close
        // again once everything is confirmed.
        e.Cancel = true;
        if (_closePromptOpen)
            return;

        _closePromptOpen = true;
        try
        {
            if (await vm.ConfirmExitAsync())
            {
                SaveLayout();
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _closePromptOpen = false;
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

            _sessionsHandler = (_, _) => UpdateSessionPanelVisibility(vm);
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
        vm.Sessions.PanelChooser = ChoosePanelAsync;

        if (!_layoutRestored)
        {
            _layoutRestored = true;
            RestoreLayoutFromSettings(vm);
        }

        _vmSubscriptions.Add(vm.ConnectionTree.EditNode.RegisterHandler(async context =>
        {
            var dialog = new ConnectionDialog(context.Input);
            context.SetOutput(await dialog.ShowDialog<bool>(this));
        }));
        _vmSubscriptions.Add(vm.ConnectionTree.Confirm.RegisterHandler(async context =>
        {
            var (title, message) = context.Input;
            context.SetOutput(await MessageDialog.ConfirmAsync(this, title, message, "Delete", "Cancel", confirmIsDefault: false));
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
        if (change.Property == ClientSizeProperty)
            RememberNormalBounds();
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
        if (DataContext is MainWindowViewModel sessionsVm && SessionKeyboard.Handle(sessionsVm.Sessions, e))
        {
            e.Handled = true;
            return;
        }
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
        // The welcome text only while nothing is open and there is at most one docked panel.
        var showArea = vm.Sessions.Sessions.Count > 0 || vm.Sessions.Panels.Count(p => !p.IsFloating) > 1;
        EmptySessionsPanel.IsVisible = !showArea;
        SessionArea.IsVisible = showArea;
    }

    // ── Sessions, panels, favorites ───────────────────────────────────────

    /// <summary>Legacy frmChoosePanel, used by "Connect to panel" and "always show panel selection".</summary>
    private async Task<string?> ChoosePanelAsync(IReadOnlyList<string> panels, string suggestion)
    {
        if (!IsVisible) return suggestion;
        return await new ChoosePanelDialog(panels, suggestion).ShowDialog<string?>(this);
    }

    /// <summary>Lists the open sessions (grouped by panel) at the end of the Sessions menu.</summary>
    public void RebuildSessionList()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var items = SessionsMenu.Items;
        var start = items.IndexOf(SessionListSeparator) + 1;
        while (items.Count > start)
            items.RemoveAt(items.Count - 1);

        var dock = vm.Sessions;
        var panels = dock.Panels.Where(p => p.Sessions.Count > 0).ToList();
        if (panels.Count == 0)
        {
            items.Add(new MenuItem { Header = "(No open sessions)", IsEnabled = false });
            return;
        }

        foreach (var panel in panels)
        {
            if (panels.Count > 1)
                items.Add(new MenuItem { Header = panel.Name, IsEnabled = false, FontWeight = global::Avalonia.Media.FontWeight.Bold });
            for (var i = 0; i < panel.Sessions.Count; i++)
            {
                var session = panel.Sessions[i];
                var item = new MenuItem
                {
                    Header = session.Title,
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = ReferenceEquals(dock.ActiveSession, session),
                    Icon = new Image { Source = session.Icon, Width = 16, Height = 16 },
                };
                if (ReferenceEquals(panel, dock.ActivePanel) && i < 9)
                    item.InputGesture = new KeyGesture(Key.D1 + i, KeyModifiers.Control);
                item.Click += (_, _) => dock.ActiveSession = session;
                items.Add(item);
            }
        }
    }

    /// <summary>The Favorites menu: every connection with Favorite set, refreshed when it opens.</summary>
    public void RebuildFavorites()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var items = FavoritesMenu.Items;
        items.Clear();
        var favorites = vm.GetFavorites();
        if (favorites.Count == 0)
        {
            items.Add(new MenuItem { Header = "(No favorites)", IsEnabled = false });
            return;
        }

        foreach (var connection in favorites)
        {
            var item = new MenuItem
            {
                Header = connection.Name,
                Icon = new Image { Source = IconService.GetProtocolIcon(connection.Protocol.ToString()), Width = 16, Height = 16 },
            };
            ToolTip.SetTip(item, $"{connection.Protocol} {connection.Hostname}");
            item.Click += async (_, _) => await vm.ConnectFavoriteAsync(connection);
            items.Add(item);
        }
    }

    private void UpdateArrangementChecks()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        ArrangeTabbedItem.IsChecked = vm.Sessions.Arrangement == PanelArrangement.Tabbed;
        ArrangeSideBySideItem.IsChecked = vm.Sessions.Arrangement == PanelArrangement.SideBySide;
        ArrangeStackedItem.IsChecked = vm.Sessions.Arrangement == PanelArrangement.Stacked;
    }

    private async void OnMultiSshKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var multiSsh = vm.MultiSsh;
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                await multiSsh.SendCommandAsync();
                break;
            case Key.Up when e.KeyModifiers == KeyModifiers.None:
                e.Handled = multiSsh.NavigateHistory(-1);
                MultiSshBox.CaretIndex = MultiSshBox.Text?.Length ?? 0;
                break;
            case Key.Down when e.KeyModifiers == KeyModifiers.None:
                e.Handled = multiSsh.NavigateHistory(+1);
                MultiSshBox.CaretIndex = MultiSshBox.Text?.Length ?? 0;
                break;
            case >= Key.A and <= Key.Z when e.KeyModifiers == KeyModifiers.Control && e.Key != Key.V:
                // Like the legacy toolbar: Ctrl+letter goes to the sessions (Ctrl+C interrupts), except paste.
                e.Handled = true;
                await multiSsh.SendControlAsync((char)('A' + (e.Key - Key.A)));
                break;
        }
    }

    // ── Layout persistence ────────────────────────────────────────────────

    /// <summary>The current layout: window placement, side panels, toolbars and session panels.</summary>
    public WindowLayoutState CaptureLayout()
    {
        RememberNormalBounds();
        SessionArea.CaptureWeights();
        var vm = DataContext as MainWindowViewModel;
        var state = new WindowLayoutState
        {
            X = _normalPosition?.X,
            Y = _normalPosition?.Y,
            Width = _normalSize?.Width ?? (double.IsFinite(Width) ? Width : WindowLayoutState.DefaultWidth),
            Height = _normalSize?.Height ?? (double.IsFinite(Height) ? Height : WindowLayoutState.DefaultHeight),
            WindowState = (WindowState == WindowState.FullScreen ? _stateBeforeFullScreen : WindowState) == WindowState.Maximized
                ? "Maximized"
                : "Normal",
            TreeVisible = vm?.IsConnectionTreeVisible ?? true,
            LogVisible = vm?.IsLogPanelVisible ?? true,
            TreeWidth = CurrentTreeWidth().Value,
            BottomHeight = CurrentBottomHeight().Value,
            MultiSshToolbarVisible = vm?.IsMultiSshToolbarVisible ?? false,
        };
        vm?.Sessions.CaptureLayout(state);
        return state;
    }

    /// <summary>Applies a saved layout; the window placement only when <paramref name="placement"/> is set.</summary>
    public void ApplyLayout(WindowLayoutState state, bool placement = true)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        if (placement)
        {
            Width = state.Width;
            Height = state.Height;
            _normalSize = new Size(state.Width, state.Height);
            if (state is { X: { } x, Y: { } y })
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(x, y);
                _normalPosition = Position;
            }
            if (state.WindowState == "Maximized")
                WindowState = WindowState.Maximized;
        }

        // Collapse first (re-applies the sizes even when the visibility flag does not change).
        SetTreeVisible(false);
        SetBottomPanelVisible(false);
        _treeWidth = new GridLength(state.TreeWidth);
        _bottomHeight = new GridLength(state.BottomHeight);
        vm.IsConnectionTreeVisible = state.TreeVisible;
        vm.IsLogPanelVisible = state.LogVisible;
        SetTreeVisible(state.TreeVisible);
        SetBottomPanelVisible(state.LogVisible);
        vm.IsMultiSshToolbarVisible = state.MultiSshToolbarVisible;
        vm.Sessions.ApplyLayout(state);
    }

    /// <summary>Stores the layout in the settings (called when the window closes).</summary>
    public void SaveLayout()
    {
        try
        {
            var json = CaptureLayout().ToJson();
            AppServices.GetRequired<AppSettingsService>().Update(s => s.WindowLayout = json);
        }
        catch (Exception ex)
        {
            (DataContext as MainWindowViewModel)?.LogPanel.Log($"Could not save the window layout: {ex.Message}", LogLevel.Warning);
        }
    }

    /// <summary>Restores the layout saved in the settings (at startup).</summary>
    public void RestoreLayoutFromSettings(MainWindowViewModel vm)
    {
        try
        {
            var json = AppServices.GetRequired<AppSettingsService>().Current.WindowLayout;
            if (WindowLayoutState.FromJson(json) is { } state)
                ApplyLayout(state);
        }
        catch (Exception ex)
        {
            vm.LogPanel.Log($"Could not restore the window layout: {ex.Message}", LogLevel.Warning);
        }
    }

    private GridLength CurrentTreeWidth()
    {
        var width = MainGrid.ColumnDefinitions[0].Width;
        return TreePanel.IsVisible && width.Value > 0 ? width : _treeWidth;
    }

    private GridLength CurrentBottomHeight()
    {
        var height = MainGrid.RowDefinitions[2].Height;
        return BottomPanel.IsVisible && height.Value > 0 ? height : _bottomHeight;
    }

    private void RememberNormalBounds()
    {
        if (WindowState != WindowState.Normal || !IsVisible) return;
        _normalPosition = Position;
        if (ClientSize.Width > 0 && ClientSize.Height > 0)
            _normalSize = ClientSize;
    }

    /// <summary>Moves a restored window back onto a screen when its saved position is off every screen.</summary>
    private void EnsureOnScreen()
    {
        if (Screens is null || Screens.ScreenCount == 0) return;
        if (Screens.ScreenFromWindow(this) is null && Screens.Primary is { } primary)
        {
            var area = primary.WorkingArea;
            Position = new PixelPoint(
                area.X + Math.Max(0, (area.Width - (int)Bounds.Width) / 2),
                area.Y + Math.Max(0, (area.Height - (int)Bounds.Height) / 2));
        }
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
