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

    /// <summary>Last bounds of the floating window; null for the default placement.</summary>
    public FloatingBounds? FloatingBounds { get; set; }

    public int SessionCount => Sessions.Count;

    /// <summary>Raised when <see cref="ActiveSession"/> changes.</summary>
    public event EventHandler? ActiveSessionChanged;

    internal void RaiseSessionCountChanged() => this.RaisePropertyChanged(nameof(SessionCount));

    public override string ToString() => Name;
}
