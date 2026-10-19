using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Material.Icons;
using Material.Icons.Avalonia;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Localization;
using mRemoteNG.Core.Settings;

namespace mRemoteNG.Avalonia.Views.Sessions;

/// <summary>
/// Lays out the session panels: tabbed (a panel strip, one panel visible), side by side or stacked
/// (all docked panels with splitters), a recursive split layout tree, and owns the windows of floating panels.
/// Each panel keeps a single <see cref="SessionPanelView"/> that is moved between the main window and its floating window.
/// </summary>
public partial class SessionAreaView : UserControl
{
    private const double SplitterSize = 4;

    private readonly Dictionary<SessionPanelViewModel, SessionPanelView> _views = [];
    private readonly Dictionary<SessionPanelViewModel, FloatingPanelWindow> _floating = [];
    private readonly HashSet<SessionPanelViewModel> _watchedPanels = [];
    private readonly Dictionary<SplitLayoutNode, Grid> _splitGrids = [];
    private SessionsDockable? _dock;
    private AppSettingsService? _settings;
    private List<SessionPanelViewModel> _arranged = [];
    private PanelArrangement? _arrangedMode;
    private string? _arrangedSections;
    private string? _arrangedTreeSignature;
    private bool _rebuilding;
    private bool _rebuildQueued;

    /// <summary>Which half of a two-section split is showing, when the strip is in section mode.</summary>
    private readonly Dictionary<Border, PanelSection> _sectionOfStrip = [];

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

    /// <summary>True while a drop preview is showing on any docked panel (for tests).</summary>
    public bool IsSplitPreviewVisible => _views.Values.Any(v => v.IsSplitPreviewVisible);

    private void Bind(SessionsDockable? dock)
    {
        if (ReferenceEquals(_dock, dock)) return;
        if (_dock is not null)
        {
            _dock.Panels.CollectionChanged -= OnPanelsChanged;
            _dock.PropertyChanged -= OnDockPropertyChanged;
            _dock.TextChatRequested -= OnTextChatRequested;
            foreach (var panel in _watchedPanels.ToList())
                Unwatch(panel);
        }
        if (_settings is not null)
            _settings.Changed -= OnSettingsChanged;

        _dock = dock;
        if (_dock is null) return;
        _dock.Panels.CollectionChanged += OnPanelsChanged;
        _dock.PropertyChanged += OnDockPropertyChanged;
        _dock.TextChatRequested += OnTextChatRequested;
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

    private void OnTextChatRequested(object? sender, SessionTabViewModel session) =>
        TextChatWindow.ShowFor(session, TopLevel.GetTopLevel(this) as Window);

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
        if (e.PropertyName is nameof(SessionsDockable.Arrangement) or nameof(SessionsDockable.ActivePanel)
            or nameof(SessionsDockable.LayoutTree))
        {
            Rebuild();
        }
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionPanelViewModel.IsFloating):
                Rebuild();
                break;
            case nameof(SessionPanelViewModel.Section):
                // Several panels change half together. Rebuild once, after they have all settled.
                RequestRebuild();
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

    /// <summary>Coalesces the rebuilds queued by a burst of panel changes into one.</summary>
    private void RequestRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    /// <summary>Brings the views and windows in line with the panels, their docking state and the arrangement.</summary>
    public void Rebuild()
    {
        if (_dock is null) return;
        if (_rebuilding)
        {
            // A change made while laying out (never expected) must not start a second layout on this stack.
            RequestRebuild();
            return;
        }
        _rebuilding = true;
        try
        {
            RebuildCore();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void RebuildCore()
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
            (view.Parent as Panel)?.Children.Remove(view);
            view.DataContext = null;
        }

        foreach (var panel in panels.Where(p => !_views.ContainsKey(p)))
            _views[panel] = new SessionPanelView(panel);

        var docked = panels.Where(p => !p.IsFloating).ToList();
        var sections = SectionSignature(docked);
        var treeSig = TreeSignature(docked);
        if (_arrangedMode != _dock.Arrangement || !_arranged.SequenceEqual(docked)
            || _arrangedSections != sections || _arrangedTreeSignature != treeSig)
        {
            Arrange(docked);
        }

        var toFloat = panels.Where(p => p.IsFloating && !_floating.ContainsKey(p)).ToList();
        if (toFloat.Count > 0)
        {
            // Views about to move to their own window must leave this window's layout queue first.
            foreach (var panel in toFloat)
                (_views[panel].Parent as Panel)?.Children.Remove(_views[panel]);
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
        _arrangedSections = SectionSignature(docked);
        _arrangedTreeSignature = TreeSignature(docked);
        _sectionOfStrip.Clear();
        _splitGrids.Clear();
        foreach (var view in _views.Values)
            (view.Parent as Panel)?.Children.Remove(view);
        PanelGrid.Children.Clear();
        PanelGrid.RowDefinitions.Clear();
        PanelGrid.ColumnDefinitions.Clear();

        if (EffectiveSplitTree(docked) is { } tree)
        {
            PopulateSplitNode(PanelGrid, tree, docked);
            return;
        }

        if (IsTwoSectionMode && docked.Count > 1)
        {
            ArrangeSections(docked);
            return;
        }

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
                AddSplitter(PanelGrid, columns, i * 2 - 1);

            AddDefinition(PanelGrid, columns, new GridLength(docked[i].SizeWeight, GridUnitType.Star));
            var view = _views[docked[i]];
            Place(view, columns, i * 2);
            PanelGrid.Children.Add(view);
        }
    }

    private SplitLayoutNode? EffectiveSplitTree(IReadOnlyList<SessionPanelViewModel> docked)
    {
        if (!IsTwoSectionMode || docked.Count <= 1 || _dock?.LayoutTree is not { } raw)
            return null;
        var validNames = docked.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pruned = raw.Pruned(validNames);
        if (pruned is null || pruned.IsLeaf)
            return null;
        // Ensure all docked panels are in the tree; if user added a named panel outside the tree, fall back to section mode unless all are in tree.
        return docked.All(p => pruned.ContainsPanel(p.Name)) ? pruned : null;
    }

    private string? TreeSignature(IReadOnlyList<SessionPanelViewModel> docked) =>
        EffectiveSplitTree(docked)?.Signature();

    private void PopulateSplitNode(Grid grid, SplitLayoutNode node, IReadOnlyList<SessionPanelViewModel> docked)
    {
        if (node.IsLeaf)
        {
            if (docked.FirstOrDefault(p => string.Equals(p.Name, node.PanelName, StringComparison.OrdinalIgnoreCase)) is { } panel)
                grid.Children.Add(_views[panel]);
            return;
        }

        _splitGrids[node] = grid;
        var columns = node.Vertical;
        var ratio = SplitLayoutNode.ClampRatio(node.Ratio);
        AddDefinition(grid, columns, new GridLength(ratio, GridUnitType.Star));
        AddSplitter(grid, columns, 1);
        AddDefinition(grid, columns, new GridLength(1 - ratio, GridUnitType.Star));

        PlaceChildNode(grid, node.First!, columns, 0, docked);
        PlaceChildNode(grid, node.Second!, columns, 2, docked);
    }

    private void PlaceChildNode(Grid parentGrid, SplitLayoutNode child, bool columns, int index, IReadOnlyList<SessionPanelViewModel> docked)
    {
        if (child.IsLeaf)
        {
            if (docked.FirstOrDefault(p => string.Equals(p.Name, child.PanelName, StringComparison.OrdinalIgnoreCase)) is { } panel)
            {
                var view = _views[panel];
                Place(view, columns, index);
                parentGrid.Children.Add(view);
            }
            return;
        }

        var subGrid = new Grid();
        Place(subGrid, columns, index);
        parentGrid.Children.Add(subGrid);
        PopulateSplitNode(subGrid, child, docked);
    }

    /// <summary>
    /// Two halves, each a tabbed section. Panels marked <see cref="PanelSection.Secondary"/> go to the
    /// right (or the bottom); everything else stays in the first half.
    /// </summary>
    private void ArrangeSections(List<SessionPanelViewModel> docked)
    {
        var columns = _dock!.Arrangement == PanelArrangement.SplitVertical;
        var primary = docked.Where(p => p.Section != PanelSection.Secondary).ToList();
        var secondary = docked.Where(p => p.Section == PanelSection.Secondary).ToList();
        if (primary.Count == 0 || secondary.Count == 0)
        {
            primary = docked.Take(1).ToList();
            secondary = docked.Skip(1).ToList();
        }

        AddDefinition(PanelGrid, columns, new GridLength(WeightOf(primary), GridUnitType.Star));
        AddSplitter(PanelGrid, columns, 1);
        AddDefinition(PanelGrid, columns, new GridLength(WeightOf(secondary), GridUnitType.Star));

        PlaceSection(primary, PanelSection.Primary, columns, 0);
        PlaceSection(secondary, PanelSection.Secondary, columns, 2);
    }

    private void PlaceSection(List<SessionPanelViewModel> panels, PanelSection section, bool columns, int index)
    {
        var host = new Grid();
        Place(host, columns, index);
        PanelGrid.Children.Add(host);
        if (panels.Count == 0)
            return;

        if (panels.Count > 1)
        {
            var strip = new Border();
            strip.Classes.Add("panel-strip");
            _sectionOfStrip[strip] = section;
            host.Children.Add(strip);
            Grid.SetRow(strip, 0);
        }

        foreach (var panel in panels)
        {
            var view = _views[panel];
            host.Children.Add(view);
            if (panels.Count > 1)
                Grid.SetRow(view, 1);
        }
        if (panels.Count > 1)
        {
            host.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            host.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        }
    }

    /// <summary>Changes that force a re-layout of the two halves (a panel switching section).</summary>
    private string? SectionSignature(IReadOnlyList<SessionPanelViewModel> docked) =>
        IsTwoSectionMode
            ? string.Join("|", docked.Select(p => p.Section == PanelSection.Secondary ? "1" : "0"))
            : null;

    private bool IsTwoSectionMode =>
        _dock?.Arrangement is PanelArrangement.SplitVertical or PanelArrangement.SplitHorizontal;

    private bool IsTwoSections => IsTwoSectionMode && _arranged.Count > 1 && _splitGrids.Count == 0;

    private bool IsTreeSplit => _splitGrids.Count > 0;

    private static double WeightOf(IReadOnlyList<SessionPanelViewModel> panels) =>
        panels.Count == 0 ? 1 : panels.Max(p => p.SizeWeight);

    private void AddSplitter(Grid grid, bool columns, int index)
    {
        AddDefinition(grid, columns, new GridLength(SplitterSize));
        var splitter = new GridSplitter
        {
            ResizeDirection = columns ? GridResizeDirection.Columns : GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        };
        splitter.Classes.Add("panel-splitter");
        splitter.Classes.Add(columns ? "columns" : "rows");
        if (columns) splitter.Width = SplitterSize; else splitter.Height = SplitterSize;
        splitter.DragCompleted += (_, _) => CaptureWeights();
        Place(splitter, columns, index);
        grid.Children.Add(splitter);
    }

    private static void AddDefinition(Grid grid, bool columns, GridLength length)
    {
        if (columns)
            grid.ColumnDefinitions.Add(new ColumnDefinition(length));
        else
            grid.RowDefinitions.Add(new RowDefinition(length));
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
        if (IsTreeSplit)
        {
            foreach (var (node, grid) in _splitGrids)
            {
                var first = node.Vertical
                    ? grid.ColumnDefinitions.ElementAtOrDefault(0)?.ActualWidth ?? 0
                    : grid.RowDefinitions.ElementAtOrDefault(0)?.ActualHeight ?? 0;
                var second = node.Vertical
                    ? grid.ColumnDefinitions.ElementAtOrDefault(2)?.ActualWidth ?? 0
                    : grid.RowDefinitions.ElementAtOrDefault(2)?.ActualHeight ?? 0;
                if (first > 0 && second > 0)
                    node.Ratio = SplitLayoutNode.ClampRatio(first / (first + second));
            }
            return;
        }
        if (IsTwoSections)
        {
            var columns = _dock!.Arrangement == PanelArrangement.SplitVertical;
            var primary = columns ? PanelGrid.ColumnDefinitions[0].ActualWidth : PanelGrid.RowDefinitions[0].ActualHeight;
            var secondary = columns ? PanelGrid.ColumnDefinitions[2].ActualWidth : PanelGrid.RowDefinitions[2].ActualHeight;
            foreach (var panel in _arranged.Where(p => p.Section != PanelSection.Secondary))
                if (primary > 0) panel.SizeWeight = primary;
            foreach (var panel in _arranged.Where(p => p.Section == PanelSection.Secondary))
                if (secondary > 0) panel.SizeWeight = secondary;
            return;
        }
        var columnsSide = _dock!.Arrangement == PanelArrangement.SideBySide;
        for (var i = 0; i < _arranged.Count; i++)
        {
            var size = columnsSide ? PanelGrid.ColumnDefinitions[i * 2].ActualWidth : PanelGrid.RowDefinitions[i * 2].ActualHeight;
            if (size > 0)
                _arranged[i].SizeWeight = size;
        }
    }

    private void ApplyWeights()
    {
        if (!IsSplit || IsTreeSplit) return;
        if (IsTwoSections)
        {
            var columns = _dock!.Arrangement == PanelArrangement.SplitVertical;
            var first = new GridLength(WeightOf(PanelsOf(PanelSection.Primary)), GridUnitType.Star);
            var second = new GridLength(WeightOf(PanelsOf(PanelSection.Secondary)), GridUnitType.Star);
            if (columns)
            {
                PanelGrid.ColumnDefinitions[0].Width = first;
                PanelGrid.ColumnDefinitions[2].Width = second;
            }
            else
            {
                PanelGrid.RowDefinitions[0].Height = first;
                PanelGrid.RowDefinitions[2].Height = second;
            }
            return;
        }
        var columnsSide = _dock!.Arrangement == PanelArrangement.SideBySide;
        for (var i = 0; i < _arranged.Count; i++)
        {
            var length = new GridLength(_arranged[i].SizeWeight, GridUnitType.Star);
            if (columnsSide) PanelGrid.ColumnDefinitions[i * 2].Width = length;
            else PanelGrid.RowDefinitions[i * 2].Height = length;
        }
    }

    private void UpdateVisibility()
    {
        if (_dock is null) return;
        if (IsTreeSplit)
        {
            foreach (var panel in _arranged)
            {
                var view = _views[panel];
                view.IsVisible = true;
                view.ShowHeader = false;
            }
            return;
        }
        if (IsTwoSections)
        {
            foreach (var (host, section) in SectionHosts())
            {
                var panels = PanelsOf(section);
                var visible = VisibleIn(panels);
                foreach (var panel in panels)
                {
                    var view = _views[panel];
                    view.IsVisible = ReferenceEquals(panel, visible);
                    view.ShowHeader = false;
                }
                if (host.Children.OfType<Border>().FirstOrDefault(b => b.Classes.Contains("panel-strip")) is { } strip)
                    FillSectionStrip(strip, panels, visible);
            }
            return;
        }

        var split = IsSplit;
        var shown = split ? null : VisibleTabbedPanel();
        foreach (var panel in _arranged)
        {
            var view = _views[panel];
            view.IsVisible = split || ReferenceEquals(panel, shown);
            view.ShowHeader = split && !panel.IsSplitPanel;
        }
    }

    /// <summary>The grid of each half, with the section it shows.</summary>
    private IEnumerable<(Grid Host, PanelSection Section)> SectionHosts()
    {
        foreach (var host in PanelGrid.Children.OfType<Grid>())
        {
            if (host.Children.OfType<Border>().FirstOrDefault(b => _sectionOfStrip.ContainsKey(b)) is { } strip)
                yield return (host, _sectionOfStrip[strip]);
            else if (host.Children.OfType<SessionPanelView>().FirstOrDefault() is { } view
                     && _views.FirstOrDefault(kv => ReferenceEquals(kv.Value, view)).Key is { } panel)
                yield return (host, panel.Section == PanelSection.Secondary ? PanelSection.Secondary : PanelSection.Primary);
        }
    }

    private List<SessionPanelViewModel> PanelsOf(PanelSection section)
    {
        var primary = _arranged.Where(p => p.Section != PanelSection.Secondary).ToList();
        var secondary = _arranged.Where(p => p.Section == PanelSection.Secondary).ToList();
        if (primary.Count == 0 || secondary.Count == 0)
        {
            primary = _arranged.Take(1).ToList();
            secondary = _arranged.Skip(1).ToList();
        }
        return section == PanelSection.Secondary ? secondary : primary;
    }

    /// <summary>The panel a section shows: the active one when it belongs here, else the first.</summary>
    private SessionPanelViewModel? VisibleIn(IReadOnlyList<SessionPanelViewModel> panels)
    {
        if (_dock?.ActivePanel is { } active && panels.Contains(active))
            return active;
        return panels.FirstOrDefault();
    }

    private void FillSectionStrip(Border strip, IReadOnlyList<SessionPanelViewModel> panels, SessionPanelViewModel? visible)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var panel in panels.Where(p => !p.IsSplitPanel))
        {
            var button = PanelTab(panel, ReferenceEquals(panel, visible));
            button.Click += (_, _) =>
            {
                _dock!.ActivePanel = panel;
                UpdateVisibility();
            };
            bar.Children.Add(button);
        }
        strip.Child = bar;
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
        var namedPanels = _arranged.Where(p => !p.IsSplitPanel).ToList();
        var show = !IsSplit && (namedPanels.Count > 1 || (namedPanels.Count == 1 && _dock.Settings?.AlwaysShowPanelTabs == true));
        PanelStripBar.IsVisible = show;
        PanelStrip.Children.Clear();
        if (!show) return;

        var visible = VisibleTabbedPanel();
        foreach (var panel in namedPanels)
        {
            var button = PanelTab(panel, ReferenceEquals(panel, visible));
            button.Click += (_, _) =>
            {
                _dock.ActivePanel = panel;
                Rebuild();
            };
            PanelStrip.Children.Add(button);
        }

        var add = new Button
        {
            Content = new MaterialIcon { Kind = MaterialIconKind.Plus },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        add.Classes.Add("icon");
        ToolTip.SetTip(add, Localizer.Get("NewPanel", "New panel"));
        add.Click += (_, _) => _dock.NewPanel();
        AcceptTabDrops(add, (_, _) => true,
            (dock, tab) => dock.MoveSession(tab, dock.UniquePanelName(SessionsDockable.NewPanelBaseName)));
        PanelStrip.Children.Add(add);
    }

    /// <summary>A panel tab: selects the panel, offers its menu, and accepts dropped session tabs.</summary>
    private ToggleButton PanelTab(SessionPanelViewModel panel, bool isChecked)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new TextBlock { Text = panel.Name, VerticalAlignment = VerticalAlignment.Center });
        if (panel.SessionCount > 0)
        {
            var badge = new Border { Child = new TextBlock { Text = panel.SessionCount.ToString(CultureInfo.CurrentCulture) } };
            badge.Classes.Add("badge");
            content.Children.Add(badge);
        }
        var button = new ToggleButton { Content = content, IsChecked = isChecked, Tag = panel };
        button.Classes.Add("panel-tab");
        ToolTip.SetTip(button, Localizer.Get("RightClickForPanelOptions"));
        button.ContextRequested += (_, e) =>
        {
            SessionPanelView.BuildPanelMenu(panel, TopLevel.GetTopLevel(this) as Window).Open(button);
            e.Handled = true;
        };
        AcceptTabDrops(button, (_, tab) => !ReferenceEquals(tab.Panel, panel),
            (dock, tab) => dock.MoveSession(tab, panel, panel.Sessions.Count));
        return button;
    }

    /// <summary>Lets session tabs be dropped on a panel strip button (to move them into that panel or a new one).</summary>
    private void AcceptTabDrops(Control target, Func<SessionsDockable, SessionTabViewModel, bool> accepts,
        Action<SessionsDockable, SessionTabViewModel> drop)
    {
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var ok = _dock is { } dock && SessionPanelView.DraggedTab(e, dock) is { } tab && accepts(dock, tab);
            e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
            target.Classes.Set("drop-target", ok);
            e.Handled = true;
        });
        target.AddHandler(DragDrop.DragLeaveEvent, (_, _) => target.Classes.Set("drop-target", false));
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            target.Classes.Set("drop-target", false);
            if (_dock is { } dock && SessionPanelView.DraggedTab(e, dock) is { } tab && accepts(dock, tab))
            {
                drop(dock, tab);
                e.Handled = true;
            }
        });
    }

    private void Float(SessionPanelViewModel panel)
    {
        var view = _views[panel];
        (view.Parent as Panel)?.Children.Remove(view);
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
