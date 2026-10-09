using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Settings;

namespace mRemoteNG.Avalonia.Views.Sessions;

/// <summary>
/// Lays out the session panels: tabbed (a panel strip, one panel visible), side by side or stacked
/// (all docked panels with splitters), and owns the windows of floating panels. Each panel keeps a
/// single <see cref="SessionPanelView"/> that is moved between the main window and its floating window.
/// </summary>
public partial class SessionAreaView : UserControl
{
    private const double SplitterSize = 4;

    private readonly Dictionary<SessionPanelViewModel, SessionPanelView> _views = [];
    private readonly Dictionary<SessionPanelViewModel, FloatingPanelWindow> _floating = [];
    private readonly HashSet<SessionPanelViewModel> _watchedPanels = [];
    private SessionsDockable? _dock;
    private AppSettingsService? _settings;
    private List<SessionPanelViewModel> _arranged = [];
    private PanelArrangement? _arrangedMode;

    public SessionAreaView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind(DataContext as SessionsDockable);
    }

    /// <summary>Raised after panels were (re)arranged, docked, floated, added or removed.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>The view of each panel (for tests).</summary>
    public IReadOnlyDictionary<SessionPanelViewModel, SessionPanelView> PanelViews => _views;

    /// <summary>The window of each floating panel (for tests).</summary>
    public IReadOnlyDictionary<SessionPanelViewModel, FloatingPanelWindow> FloatingWindows => _floating;

    /// <summary>The docked panels in display order.</summary>
    public IReadOnlyList<SessionPanelViewModel> DockedPanels => _arranged;

    public bool IsPanelStripVisible => PanelStripBar.IsVisible;

    private void Bind(SessionsDockable? dock)
    {
        if (ReferenceEquals(_dock, dock)) return;
        if (_dock is not null)
        {
            _dock.Panels.CollectionChanged -= OnPanelsChanged;
            _dock.PropertyChanged -= OnDockPropertyChanged;
            foreach (var panel in _watchedPanels.ToList())
                Unwatch(panel);
        }
        if (_settings is not null)
            _settings.Changed -= OnSettingsChanged;

        _dock = dock;
        if (_dock is null) return;
        _dock.Panels.CollectionChanged += OnPanelsChanged;
        _dock.PropertyChanged += OnDockPropertyChanged;
        foreach (var panel in _dock.Panels)
            Watch(panel);
        try
        {
            _settings = AppServices.Provider.GetService(typeof(AppSettingsService)) as AppSettingsService;
        }
        catch (InvalidOperationException)
        {
            _settings = null; // design time: no container
        }
        if (_settings is not null)
            _settings.Changed += OnSettingsChanged;
        Rebuild();
    }

    private void Watch(SessionPanelViewModel panel)
    {
        if (_watchedPanels.Add(panel))
            panel.PropertyChanged += OnPanelPropertyChanged;
    }

    private void Unwatch(SessionPanelViewModel panel)
    {
        if (_watchedPanels.Remove(panel))
            panel.PropertyChanged -= OnPanelPropertyChanged;
    }

    private void OnPanelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_dock is null) return;
        foreach (var panel in _watchedPanels.Where(p => !_dock.Panels.Contains(p)).ToList())
            Unwatch(panel);
        foreach (var panel in _dock.Panels)
            Watch(panel);
        Rebuild();
    }

    private void OnDockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionsDockable.Arrangement) or nameof(SessionsDockable.ActivePanel))
            Rebuild();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionPanelViewModel.IsFloating):
                Rebuild();
                break;
            case nameof(SessionPanelViewModel.Name):
            case nameof(SessionPanelViewModel.SessionCount):
                UpdatePanelStrip();
                break;
            case nameof(SessionPanelViewModel.SizeWeight):
                ApplyWeights();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, AppSettingsChangedEventArgs e)
    {
        if (e.Changed(s => s.AlwaysShowPanelTabs))
            global::Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
    }

    /// <summary>Brings the views and windows in line with the panels, their docking state and the arrangement.</summary>
    public void Rebuild()
    {
        if (_dock is null) return;
        var panels = _dock.Panels.ToList();

        // Windows of panels that were removed or docked give their view back first.
        foreach (var (panel, window) in _floating.Where(kv => !panels.Contains(kv.Key) || !kv.Key.IsFloating).ToList())
        {
            _floating.Remove(panel);
            window.DetachAndClose();
        }

        // Views of removed panels release their sessions' controls.
        foreach (var panel in _views.Keys.Where(p => !panels.Contains(p)).ToList())
        {
            var view = _views[panel];
            _views.Remove(panel);
            PanelGrid.Children.Remove(view);
            view.DataContext = null;
        }

        foreach (var panel in panels.Where(p => !_views.ContainsKey(p)))
            _views[panel] = new SessionPanelView(panel);

        var docked = panels.Where(p => !p.IsFloating).ToList();
        if (_arrangedMode != _dock.Arrangement || !_arranged.SequenceEqual(docked))
            Arrange(docked);

        var toFloat = panels.Where(p => p.IsFloating && !_floating.ContainsKey(p)).ToList();
        if (toFloat.Count > 0)
        {
            // Views about to move to their own window must leave this window's layout queue first.
            foreach (var panel in toFloat)
                PanelGrid.Children.Remove(_views[panel]);
            LayoutFlush.Run(LayoutFlush.RootOf(this));
            foreach (var panel in toFloat)
                Float(panel);
        }

        UpdateVisibility();
        UpdatePanelStrip();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Arrange(List<SessionPanelViewModel> docked)
    {
        _arranged = docked;
        _arrangedMode = _dock!.Arrangement;
        PanelGrid.Children.Clear();
        PanelGrid.RowDefinitions.Clear();
        PanelGrid.ColumnDefinitions.Clear();

        if (_dock.Arrangement == PanelArrangement.Tabbed || docked.Count <= 1)
        {
            foreach (var panel in docked)
                PanelGrid.Children.Add(_views[panel]);
            return;
        }

        var columns = _dock.Arrangement == PanelArrangement.SideBySide;
        for (var i = 0; i < docked.Count; i++)
        {
            if (i > 0)
            {
                AddDefinition(columns, new GridLength(SplitterSize));
                var splitter = new GridSplitter
                {
                    ResizeDirection = columns ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                    ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                };
                splitter.Classes.Add("panel-splitter");
                if (columns) splitter.Width = SplitterSize; else splitter.Height = SplitterSize;
                splitter.DragCompleted += (_, _) => CaptureWeights();
                Place(splitter, columns, i * 2 - 1);
                PanelGrid.Children.Add(splitter);
            }

            AddDefinition(columns, new GridLength(docked[i].SizeWeight, GridUnitType.Star));
            var view = _views[docked[i]];
            Place(view, columns, i * 2);
            PanelGrid.Children.Add(view);
        }
    }

    private void AddDefinition(bool columns, GridLength length)
    {
        if (columns)
            PanelGrid.ColumnDefinitions.Add(new ColumnDefinition(length));
        else
            PanelGrid.RowDefinitions.Add(new RowDefinition(length));
    }

    private static void Place(Control control, bool columns, int index)
    {
        Grid.SetColumn(control, columns ? index : 0);
        Grid.SetRow(control, columns ? 0 : index);
    }

    private bool IsSplit => _dock is not null && _dock.Arrangement != PanelArrangement.Tabbed && _arranged.Count > 1;

    /// <summary>Stores the splitter positions as panel weights (persisted with the layout).</summary>
    public void CaptureWeights()
    {
        if (!IsSplit) return;
        var columns = _dock!.Arrangement == PanelArrangement.SideBySide;
        for (var i = 0; i < _arranged.Count; i++)
        {
            var size = columns ? PanelGrid.ColumnDefinitions[i * 2].ActualWidth : PanelGrid.RowDefinitions[i * 2].ActualHeight;
            if (size > 0)
                _arranged[i].SizeWeight = size;
        }
    }

    private void ApplyWeights()
    {
        if (!IsSplit) return;
        var columns = _dock!.Arrangement == PanelArrangement.SideBySide;
        for (var i = 0; i < _arranged.Count; i++)
        {
            var length = new GridLength(_arranged[i].SizeWeight, GridUnitType.Star);
            if (columns) PanelGrid.ColumnDefinitions[i * 2].Width = length;
            else PanelGrid.RowDefinitions[i * 2].Height = length;
        }
    }

    private void UpdateVisibility()
    {
        if (_dock is null) return;
        var split = IsSplit;
        var visible = split ? null : VisibleTabbedPanel();
        foreach (var panel in _arranged)
        {
            var view = _views[panel];
            view.IsVisible = split || ReferenceEquals(panel, visible);
            view.ShowHeader = split;
        }
    }

    /// <summary>In the tabbed arrangement: the active panel when docked, else the first docked one.</summary>
    private SessionPanelViewModel? VisibleTabbedPanel()
    {
        if (_dock?.ActivePanel is { IsFloating: false } active && _arranged.Contains(active))
            return active;
        return _arranged.FirstOrDefault();
    }

    private void UpdatePanelStrip()
    {
        if (_dock is null) return;
        var show = !IsSplit && (_arranged.Count > 1 || (_arranged.Count == 1 && _dock.Settings?.AlwaysShowPanelTabs == true));
        PanelStripBar.IsVisible = show;
        PanelStrip.Children.Clear();
        if (!show) return;

        var visible = VisibleTabbedPanel();
        foreach (var panel in _arranged)
        {
            var button = new ToggleButton
            {
                Content = panel.SessionCount > 0 ? $"{panel.Name} ({panel.SessionCount})" : panel.Name,
                IsChecked = ReferenceEquals(panel, visible),
                Tag = panel,
            };
            button.Classes.Add("panel-tab");
            ToolTip.SetTip(button, "Right-click for panel options");
            button.Click += (_, _) =>
            {
                _dock.ActivePanel = panel;
                Rebuild();
            };
            button.ContextRequested += (_, e) =>
            {
                SessionPanelView.BuildPanelMenu(panel, TopLevel.GetTopLevel(this) as Window).Open(button);
                e.Handled = true;
            };
            PanelStrip.Children.Add(button);
        }

        var add = new Button
        {
            Content = "+",
            Background = global::Avalonia.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(add, "New panel");
        add.Click += (_, _) => _dock.NewPanel();
        PanelStrip.Children.Add(add);
    }

    private void Float(SessionPanelViewModel panel)
    {
        var view = _views[panel];
        PanelGrid.Children.Remove(view);
        view.IsVisible = true;
        var window = new FloatingPanelWindow(panel, view);
        _floating[panel] = window;
        ShowWhenOwnerReady(window);
    }

    private void ShowWhenOwnerReady(FloatingPanelWindow window)
    {
        if (TopLevel.GetTopLevel(this) is Window { IsVisible: true } owner)
        {
            window.Show(owner);
            return;
        }

        if (TopLevel.GetTopLevel(this) is Window hidden)
        {
            void OnOpened(object? sender, EventArgs e)
            {
                hidden.Opened -= OnOpened;
                if (_floating.ContainsValue(window))
                    window.Show(hidden);
            }
            hidden.Opened += OnOpened;
            return;
        }

        void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            AttachedToVisualTree -= OnAttached;
            if (_floating.ContainsValue(window))
                ShowWhenOwnerReady(window);
        }
        AttachedToVisualTree += OnAttached;
    }

    /// <summary>Keyboard shortcuts for floating windows and the main window.</summary>
    public bool HandleKey(KeyEventArgs e) => _dock is not null && SessionKeyboard.Handle(_dock, e);
}
