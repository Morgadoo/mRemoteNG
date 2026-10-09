using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security.Factories;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Adds imported nodes to the live connection tree — the folder selected in the tree (or the folder of the
/// selected connection), else the root — the same way File ▸ Import does. Used by tool windows such as the port
/// scanner that produce connections themselves.
/// </summary>
public sealed class ConnectionTreeImporter(ConnectionTreeViewModel tree, ConnectionImportService importService, LogPanelDockable? log = null)
{
    public static ConnectionTreeImporter ForApp() => new(
        AppServices.GetRequired<ConnectionTreeViewModel>(),
        new ConnectionImportService(AppServices.GetRequired<ICryptoProviderFactory>()),
        AppServices.GetRequired<LogPanelDockable>());

    /// <summary>Runs <paramref name="importer"/> into the target folder; returns the result and the folder's name.</summary>
    /// <exception cref="InvalidOperationException">No connection file is open.</exception>
    public (ImportResult Result, string FolderName) Import(IConnectionImporter importer, string description)
    {
        var (node, container) = TargetFolder();
        var result = importService.Import(importer, "", container);
        if (node is not null) node.IsExpanded = true;
        tree.MarkDirty();
        log?.Log($"Imported {result.Summary} from {description} into \"{container.Name}\".");
        foreach (var warning in result.Warnings)
            log?.Log($"Import: {warning}", LogLevel.Warning);
        return (result, container.Name);
    }

    private (ConnectionNodeViewModel? Node, ContainerInfo Container) TargetFolder()
    {
        var selected = tree.SelectedNode;
        if (selected?.Model is ContainerInfo folder)
            return (selected, folder);
        if (selected?.Model.Parent is ContainerInfo parent and not RootNodeInfo && selected.Parent is { } parentNode)
            return (parentNode, parent);

        var rootNode = tree.Nodes.FirstOrDefault();
        if (rootNode?.Model is ContainerInfo root)
            return (rootNode, root);
        throw new InvalidOperationException("There is no connection tree to import into.");
    }
}
