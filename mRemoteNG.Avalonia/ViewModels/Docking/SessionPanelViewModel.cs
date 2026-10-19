using System.Collections.ObjectModel;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

/// <summary>How the docked session panels share the session area.</summary>
public enum PanelArrangement
{
    /// <summary>One panel at a time, chosen with the panel tab strip (legacy default).</summary>
    Tabbed,

    /// <summary>All docked panels next to each other (columns).</summary>
    SideBySide,

    /// <summary>All docked panels above each other (rows).</summary>
    Stacked,

    /// <summary>Two halves: the left section and the right section, each with its own tabs.</summary>
    SplitVertical,

    /// <summary>Two halves: the top section and the bottom section, each with its own tabs.</summary>
    SplitHorizontal,
}

/// <summary>Which half of a split session area a panel belongs to.</summary>
public enum PanelSection
{
    /// <summary>The only section, or the left / top half.</summary>
    Primary,

    /// <summary>The right half (vertical split) or the bottom half (horizontal split).</summary>
    Secondary,
}

/// <summary>Position and size of a floating panel window.</summary>
public sealed record FloatingBounds(double X, double Y, double Width, double Height);

/// <summary>
/// A named session panel (legacy "connection panel"): its own group of session tabs. Connections
/// open in the panel named by their Panel property. A panel is docked in the main window or floats
/// in its own window.
/// </summary>
public sealed class SessionPanelViewModel : ReactiveObject
{
    private string _name;
    private SessionTabViewModel? _activeSession;
    private bool _isFloating;
    private bool _isActive;
    private double _sizeWeight = 1;
    private PanelSection _section = PanelSection.Primary;

    public SessionPanelViewModel(SessionsDockable owner, string name)
    {
        Owner = owner;
        _name = name;
    }

    public SessionsDockable Owner { get; }

    public string Name
    {
        get => _name;
        internal set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public ObservableCollection<SessionTabViewModel> Sessions { get; } = [];

    public SessionTabViewModel? ActiveSession
    {
        get => _activeSession;
        set
        {
            if (value is not null && !Sessions.Contains(value)) return;
            if (ReferenceEquals(_activeSession, value)) return;
            this.RaiseAndSetIfChanged(ref _activeSession, value);
            ActiveSessionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The panel shows in its own window instead of the main window.</summary>
    public bool IsFloating
    {
        get => _isFloating;
        set => this.RaiseAndSetIfChanged(ref _isFloating, value);
    }

    /// <summary>True for the panel that has the focus (where Ctrl+Tab and new ad-hoc sessions go).</summary>
    public bool IsActive
    {
        get => _isActive;
        internal set => this.RaiseAndSetIfChanged(ref _isActive, value);
    }

    /// <summary>Relative size when panels are shown side by side or stacked (splitter position).</summary>
    public double SizeWeight
    {
        get => _sizeWeight;
        set => this.RaiseAndSetIfChanged(ref _sizeWeight, double.IsFinite(value) && value > 0.01 ? value : 1);
    }

    /// <summary>
    /// Which half the panel lives in when the session area is split in two. Ignored for the tabbed,
    /// side-by-side and stacked arrangements.
    /// </summary>
    public PanelSection Section
    {
        get => _section;
        set => SetSection(value);
    }

    /// <summary>Sets the half without notifying, so several panels can be rearranged before the layout rebuilds.</summary>
    internal void SetSection(PanelSection value)
    {
        if (_section == value) return;
        _section = value;
        this.RaisePropertyChanged(nameof(Section));
    }

    /// <summary>Last bounds of the floating window; null for the default placement.</summary>
    public FloatingBounds? FloatingBounds { get; set; }

    public int SessionCount => Sessions.Count;

    /// <summary>Raised when <see cref="ActiveSession"/> changes.</summary>
    public event EventHandler? ActiveSessionChanged;

    internal void RaiseSessionCountChanged() => this.RaisePropertyChanged(nameof(SessionCount));

    public override string ToString() => Name;
}
