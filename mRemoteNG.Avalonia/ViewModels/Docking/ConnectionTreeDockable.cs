using Dock.Model.Mvvm.Controls;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels.Docking;

/// <summary>Dock panel wrapping the connection tree.</summary>
public sealed class ConnectionTreeDockable : Tool
{
    public ConnectionTreeDockable()
    {
        Id = "ConnectionTree";
        Title = "Connections";
    }

    public ConnectionTreeViewModel Tree { get; } = new();
}
