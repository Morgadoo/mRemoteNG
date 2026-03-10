using System.Collections.ObjectModel;
using ReactiveUI;
using System.Reactive;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>
/// Represents a single node in the connection tree (folder or connection).
/// </summary>
public sealed class ConnectionNodeViewModel : ReactiveObject
{
    private string _name = string.Empty;
    private string _protocol = string.Empty;
    private string _hostname = string.Empty;
    private bool _isFolder;
    private bool _isExpanded;

    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public string Protocol
    {
        get => _protocol;
        set => this.RaiseAndSetIfChanged(ref _protocol, value);
    }

    public string Hostname
    {
        get => _hostname;
        set => this.RaiseAndSetIfChanged(ref _hostname, value);
    }

    public bool IsFolder
    {
        get => _isFolder;
        set => this.RaiseAndSetIfChanged(ref _isFolder, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

    public ObservableCollection<ConnectionNodeViewModel> Children { get; } = [];

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> EditCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    public ConnectionNodeViewModel()
    {
        ConnectCommand = ReactiveCommand.Create(OnConnect);
        EditCommand = ReactiveCommand.Create(OnEdit);
        DeleteCommand = ReactiveCommand.Create(OnDelete);
    }

    private void OnConnect() { /* Phase 2: initiate protocol connection */ }
    private void OnEdit() { /* Phase 2: open edit dialog */ }
    private void OnDelete() { /* Phase 2: confirm and remove */ }
}

/// <summary>
/// ViewModel for the connection tree panel.
/// Owns the root collection of connection nodes and tree-level commands.
/// </summary>
public sealed class ConnectionTreeViewModel : ReactiveObject
{
    private string _searchFilter = string.Empty;
    private ConnectionNodeViewModel? _selectedNode;

    public ObservableCollection<ConnectionNodeViewModel> Nodes { get; } = [];

    public string SearchFilter
    {
        get => _searchFilter;
        set => this.RaiseAndSetIfChanged(ref _searchFilter, value);
    }

    public ConnectionNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set => this.RaiseAndSetIfChanged(ref _selectedNode, value);
    }

    public ReactiveCommand<Unit, Unit> ConnectSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> NewFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> NewConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> SortCommand { get; }

    public ConnectionTreeViewModel()
    {
        var hasSelection = this.WhenAnyValue(x => x.SelectedNode, n => n != null);

        ConnectSelectedCommand = ReactiveCommand.Create(OnConnectSelected, hasSelection);
        NewFolderCommand = ReactiveCommand.Create(OnNewFolder);
        NewConnectionCommand = ReactiveCommand.Create(OnNewConnection);
        DeleteSelectedCommand = ReactiveCommand.Create(OnDeleteSelected, hasSelection);
        SortCommand = ReactiveCommand.Create(OnSort);

        // Populate with a demo root node for Phase 2 scaffolding.
        LoadDemoData();
    }

    private void LoadDemoData()
    {
        var root = new ConnectionNodeViewModel { Name = "Connections", IsFolder = true, IsExpanded = true };
        var servers = new ConnectionNodeViewModel { Name = "Servers", IsFolder = true, IsExpanded = true };
        servers.Children.Add(new ConnectionNodeViewModel
            { Name = "Web Server 01", Protocol = "SSH", Hostname = "192.168.1.10" });
        servers.Children.Add(new ConnectionNodeViewModel
            { Name = "DB Server", Protocol = "RDP", Hostname = "192.168.1.20" });
        root.Children.Add(servers);
        Nodes.Add(root);
    }

    private void OnConnectSelected() { /* Phase 2 */ }
    private void OnNewFolder() { /* Phase 2 */ }
    private void OnNewConnection() { /* Phase 2 */ }
    private void OnDeleteSelected() { /* Phase 2 */ }
    private void OnSort() { /* Phase 2 */ }
}
