using Dock.Model.Mvvm.Controls;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

/// <summary>Dock panel wrapping the connection tree.</summary>
public sealed class ConnectionTreeDockable : Tool
{
    public ConnectionTreeDockable()
    {
        Id = "ConnectionTree";
        Title = "Connections";
    }

    /// <summary>
    /// The tree ViewModel. Resolved from DI — set after construction.
    /// </summary>
    public ConnectionTreeViewModel? Tree { get; set; }
}
