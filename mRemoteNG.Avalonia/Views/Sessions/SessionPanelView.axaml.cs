using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Avalonia.Views.Dialogs;
using mRemoteNG.Core.Localization;

namespace mRemoteNG.Avalonia.Views.Sessions;

/// <summary>
/// One session panel: its tab strip and the views of its sessions. Every session keeps its
/// <see cref="SessionContentHost"/> while it is open (only the selected one is visible), so
/// switching tabs never tears down an embedded view.
/// </summary>
public partial class SessionPanelView : UserControl
{
    private readonly Dictionary<SessionTabViewModel, SessionContentHost> _hosts = [];
    private SessionPanelViewModel? _panel;

    public SessionPanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as SessionPanelViewModel);

        TabStrip.AddHandler(DoubleTappedEvent, OnTabDoubleTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        TabStrip.AddHandler(PointerReleasedEvent, OnTabPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        TabStrip.AddHandler(ContextRequestedEvent, OnTabContextRequested, RoutingStrategies.Bubble);
        TabStrip.AddHandler(PointerWheelChangedEvent, OnTabStripWheel, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, _) => Activate(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, _) => Activate(), RoutingStrategies.Bubble, handledEventsToo: true);

        FloatButton.Click += (_, _) =>
        {
            if (_panel is not null) _panel.IsFloating = !_panel.IsFloating;
        };
        ClosePanelButton.Click += async (_, _) => await ClosePanelAsync();
        PanelMenuButton.Click += (_, _) =>
        {
            if (_panel is null) return;
            var menu = BuildPanelMenu(_panel, OwnerWindow);
            menu.Open(PanelMenuButton);
        };
    }

    public SessionPanelView(SessionPanelViewModel panel) : this() => DataContext = panel;

    public SessionPanelViewModel? Panel => _panel;

    /// <summary>Show the panel header (name, float/dock, close). The session area sets it.</summary>
    public bool ShowHeader
    {
        get => Header.IsVisible;
        set => Header.IsVisible = value;
    }

    /// <summary>The host of each open session (for tests).</summary>
    public IReadOnlyDictionary<SessionTabViewModel, SessionContentHost> Hosts => _hosts;

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private void Bind(SessionPanelViewModel? panel)
    {
        if (ReferenceEquals(_panel, panel)) return;
        if (_panel is not null)
        {
            _panel.Sessions.CollectionChanged -= OnSessionsChanged;
            _panel.PropertyChanged -= OnPanelPropertyChanged;
            foreach (var session in _hosts.Keys.ToList())
                RemoveHost(session);
        }

        _panel = panel;
        if (_panel is null) return;
        _panel.Sessions.CollectionChanged += OnSessionsChanged;
        _panel.PropertyChanged += OnPanelPropertyChanged;
        foreach (var session in _panel.Sessions)
            AddHost(session);
        UpdateFloatButton();
        UpdateVisibility();
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_panel is null) return;
        // Remove first: a control can only have one parent at a time.
        foreach (var session in _hosts.Keys.Where(s => !_panel.Sessions.Contains(s)).ToList())
            RemoveHost(session);
        foreach (var session in _panel.Sessions.Where(s => !_hosts.ContainsKey(s)))
            AddHost(session);
        UpdateVisibility();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionPanelViewModel.ActiveSession))
        {
            UpdateVisibility(focus: true);
            if (_panel?.ActiveSession is { } active)
                TabStrip.ScrollIntoView(active);
        }
        else if (e.PropertyName == nameof(SessionPanelViewModel.IsFloating))
            UpdateFloatButton();
    }

    private void AddHost(SessionTabViewModel session)
    {
        var host = new SessionContentHost(session) { IsVisible = false };
        _hosts[session] = host;
        SessionHost.Children.Add(host);
    }

    private void RemoveHost(SessionTabViewModel session)
    {
        if (!_hosts.Remove(session, out var host)) return;
        host.Release();
        SessionHost.Children.Remove(host);
    }

    private void UpdateVisibility(bool focus = false)
    {
        var active = _panel?.ActiveSession;
        foreach (var (session, host) in _hosts)
            host.IsVisible = ReferenceEquals(session, active);
        var empty = _panel is null || _panel.Sessions.Count == 0;
        EmptyText.IsVisible = empty;
        TabScroller.IsVisible = !empty;

        if (focus && active?.ContentView is { } view && !active.IsDetached)
            Dispatcher.UIThread.Post(() =>
            {
                if (view.IsEffectivelyVisible)
                    view.Focus();
            });
    }

    private void UpdateFloatButton()
    {
        if (_panel is null) return;
        FloatButton.Content = _panel.IsFloating ? "⭳" : "⧉";
        ToolTip.SetTip(FloatButton, _panel.IsFloating ? Localizer.Get("DockThisPanelBack") : Localizer.Get("FloatThisPanelInItsOwnWindow"));
    }

    private void Activate()
    {
        if (_panel is not null && _panel.Owner.Panels.Contains(_panel))
            _panel.Owner.ActivePanel = _panel;
    }

    private async Task ClosePanelAsync()
    {
        if (_panel is null) return;
        var owner = OwnerWindow;
        if (_panel.Sessions.Count > 0 && owner is not null
            && !await MessageDialog.ConfirmAsync(owner, Localizer.Get("ClosePanel"),
                Localizer.Format("ConfirmClosePanelFormat", _panel.Name, _panel.Sessions.Count),
                Localizer.Get("_Close"), Localizer.Get("_Cancel")))
        {
            return;
        }
        await _panel.Owner.ClosePanelAsync(_panel);
    }

    // ── Tab strip ─────────────────────────────────────────────────────────

    private static SessionTabViewModel? TabFromEvent(RoutedEventArgs e)
    {
        for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem { DataContext: SessionTabViewModel tab })
                return tab;
            if (visual is ListBox)
                return null;
        }
        return null;
    }

    private void OnTabDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (TabFromEvent(e) is { } tab && _panel?.Owner.Settings?.DoubleClickOnTabClosesIt != false)
        {
            tab.CloseCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }

    private void OnTabStripWheel(object? sender, PointerWheelEventArgs e)
    {
        // The wheel scrolls the (horizontal) tab strip.
        if (TabStrip.Scroll is ScrollViewer scroller && scroller.Extent.Width > scroller.Viewport.Width)
        {
            var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
            scroller.Offset = new Vector(Math.Clamp(scroller.Offset.X - delta * 60, 0,
                scroller.Extent.Width - scroller.Viewport.Width), 0);
            e.Handled = true;
        }
    }

    private void OnTabPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Middle click closes the tab.
        if (e.InitialPressMouseButton == MouseButton.Middle && TabFromEvent(e) is { } tab)
        {
            tab.CloseCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }

    private void OnTabContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (TabFromEvent(e) is not { } tab || e.Source is not Control source) return;
        var menu = SessionTabMenu.Build(tab, OwnerWindow);
        menu.Open(source);
        e.Handled = true;
    }

    // ── Panel menu ────────────────────────────────────────────────────────

    /// <summary>The panel context menu (header "⋯" button and the panel tab strip).</summary>
    public static ContextMenu BuildPanelMenu(SessionPanelViewModel panel, Window? owner)
    {
        var dock = panel.Owner;
        var items = new List<Control>();

        var rename = new MenuItem { Header = Localizer.Menu("RenamePanel") + "..." };
        rename.Click += async (_, _) =>
        {
            if (owner is null) return;
            var name = await new TextPromptDialog(Localizer.Get("RenamePanel"), Localizer.Get("PanelName", "Panel name") + ":",
                    false, null, panel.Name)
                .ShowDialog<string?>(owner);
            if (name is not null && !dock.RenamePanel(panel, name))
                await new MessageDialog(Localizer.Get("RenamePanel"), Localizer.Format("PanelNameExistsFormat", name.Trim()),
                    new MessageDialogButton(Localizer.Get("_Ok"), "ok", IsDefault: true, IsCancel: true)).ShowDialog<string?>(owner);
        };
        items.Add(rename);

        var floating = new MenuItem { Header = Localizer.Menu(panel.IsFloating ? "DockPanel" : "FloatPanel") };
        floating.Click += (_, _) => panel.IsFloating = !panel.IsFloating;
        items.Add(floating);

        var newPanel = new MenuItem { Header = Localizer.Menu("NewPanel") };
        newPanel.Click += (_, _) => dock.NewPanel();
        items.Add(newPanel);

        var arrangement = new MenuItem { Header = Localizer.Menu("ArrangePanels") };
        foreach (var (mode, label) in ArrangementChoices)
        {
            var item = new MenuItem
            {
                Header = label,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = dock.Arrangement == mode,
            };
            item.Click += (_, _) => dock.Arrangement = mode;
            arrangement.Items.Add(item);
        }
        items.Add(arrangement);
        items.Add(new Separator());

        var close = new MenuItem { Header = Localizer.Menu("ClosePanel") };
        close.Click += async (_, _) =>
        {
            if (panel.Sessions.Count > 0 && owner is not null
                && !await MessageDialog.ConfirmAsync(owner, Localizer.Get("ClosePanel"),
                    Localizer.Format("ConfirmClosePanelFormat", panel.Name, panel.Sessions.Count),
                    Localizer.Get("_Close"), Localizer.Get("_Cancel")))
            {
                return;
            }
            await dock.ClosePanelAsync(panel);
        };
        items.Add(close);

        return new ContextMenu { ItemsSource = items };
    }

    public static IReadOnlyList<(PanelArrangement Mode, string Label)> ArrangementChoices =>
    [
        (PanelArrangement.Tabbed, Localizer.Menu("TabbedOnePanelAtATime")),
        (PanelArrangement.SideBySide, Localizer.Menu("SideBySide")),
        (PanelArrangement.Stacked, Localizer.Menu("Stacked")),
    ];
}
