using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using mRemoteNG.Avalonia.ViewModels.Docking;

namespace mRemoteNG.Avalonia.Docking;

/// <summary>
/// Builds the initial docking layout for mRemoteNG.
/// Layout:
///   ┌──────────────┬──────────────────────────────────┐
///   │ Connection   │  Connection Sessions (TabDock)    │
///   │ Tree         │                                   │
///   ├──────────────┤                                   │
///   │ (expandable) │                                   │
///   ├──────────────┴──────────────────────────────────┤
///   │ Error / Log panel (bottom, collapsible)          │
///   └─────────────────────────────────────────────────┘
/// </summary>
public sealed class DockFactory : Factory
{
    private readonly ConnectionTreeDockable _connectionTree;
    private readonly SessionsDockable _sessions;
    private readonly LogPanelDockable _logPanel;

    public DockFactory(
        ConnectionTreeDockable connectionTree,
        SessionsDockable sessions,
        LogPanelDockable logPanel)
    {
        _connectionTree = connectionTree;
        _sessions = sessions;
        _logPanel = logPanel;
    }

    public override IRootDock CreateLayout()
    {
        // Left panel: connection tree
        var leftDock = new ProportionalDock
        {
            Id = "LeftDock",
            Title = "Left",
            Proportion = 0.23,
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(
                new ToolDock
                {
                    Id = "TreeTool",
                    Title = "Tree",
                    Proportion = double.NaN,
                    VisibleDockables = CreateList<IDockable>(_connectionTree),
                    ActiveDockable = _connectionTree,
                    Alignment = Alignment.Left,
                }
            ),
        };

        // Bottom panel: log/errors
        var bottomDock = new ProportionalDock
        {
            Id = "BottomDock",
            Title = "Bottom",
            Proportion = 0.2,
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(
                new ToolDock
                {
                    Id = "LogTool",
                    Title = "Log",
                    Proportion = double.NaN,
                    VisibleDockables = CreateList<IDockable>(_logPanel),
                    ActiveDockable = _logPanel,
                    Alignment = Alignment.Bottom,
                }
            ),
        };

        // Center: connection sessions (tabbed)
        var centerDock = new DocumentDock
        {
            Id = "SessionsDock",
            Title = "Sessions",
            Proportion = double.NaN,
            VisibleDockables = CreateList<IDockable>(_sessions),
            ActiveDockable = _sessions,
            CanCreateDocument = false,
        };

        // Vertical split: center + bottom
        var verticalSplit = new ProportionalDock
        {
            Id = "VerticalSplit",
            Proportion = double.NaN,
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(
                centerDock,
                new ProportionalDockSplitter { Id = "BottomSplitter" },
                bottomDock
            ),
        };

        // Horizontal split: left tree + vertical split
        var horizontalSplit = new ProportionalDock
        {
            Id = "HorizontalSplit",
            Proportion = double.NaN,
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(
                leftDock,
                new ProportionalDockSplitter { Id = "LeftSplitter" },
                verticalSplit
            ),
        };

        var rootDock = CreateRootDock();
        rootDock.Id = "Root";
        rootDock.ActiveDockable = horizontalSplit;
        rootDock.DefaultDockable = horizontalSplit;
        rootDock.VisibleDockables = CreateList<IDockable>(horizontalSplit);

        return rootDock;
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["TreeTool"] = () => _connectionTree,
            ["SessionsDock"] = () => _sessions,
            ["LogTool"] = () => _logPanel,
        };

        DockableLocator = new Dictionary<string, Func<IDockable?>>
        {
            [_connectionTree.Id!] = () => _connectionTree,
            [_sessions.Id!] = () => _sessions,
            [_logPanel.Id!] = () => _logPanel,
        };

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow(),
        };

        base.InitLayout(layout);
    }
}
