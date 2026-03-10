using System.Collections.ObjectModel;
using System.Reactive.Linq;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using CoreProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;
using mRemoteNG.Core.Tree;
using mRemoteNG.Protocols.Abstractions;
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
    private int _port = 22;
    private string _username = string.Empty;
    private bool _isFolder;
    private bool _isExpanded;

    // Optional reference to sessions dock for connecting
    internal SessionsDockable? SessionsDock { private get; set; }
    internal IProtocolFactory? ProtocolFactory { private get; set; }

    /// <summary>Reference to the underlying domain model (null for unsaved nodes).</summary>
    internal ConnectionInfo? Model { get; set; }

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

    public int Port
    {
        get => _port;
        set => this.RaiseAndSetIfChanged(ref _port, value);
    }

    public string Username
    {
        get => _username;
        set => this.RaiseAndSetIfChanged(ref _username, value);
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
        var canConnect = this.WhenAnyValue(x => x.IsFolder, isFolder => !isFolder)
            .ObserveOn(RxApp.MainThreadScheduler);
        ConnectCommand = ReactiveCommand.CreateFromTask(OnConnectAsync, canConnect);
        ConnectCommand.ThrownExceptions.Subscribe(ex =>
            System.Diagnostics.Trace.TraceError($"Connect error: {ex.Message}"));
        EditCommand = ReactiveCommand.Create(OnEdit);
        DeleteCommand = ReactiveCommand.Create(OnDelete);
    }

    private async Task OnConnectAsync()
    {
        if (IsFolder || SessionsDock is null || ProtocolFactory is null) return;

        try
        {
            var protocolType = ResolveProtocolType(Protocol);
            var parameters = new ConnectionParameters
            {
                Hostname = Hostname,
                Port = Port == 0 ? DefaultPortFor(protocolType) : Port,
                Protocol = protocolType,
                Username = string.IsNullOrEmpty(Username) ? null : Username,
            };

            await SessionsDock.OpenConnectionAsync(parameters, ProtocolFactory);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Connection failed: {ex.Message}");
        }
    }

    private void OnEdit() { /* Phase 4: open ConnectionDialog pre-filled with this node */ }
    private void OnDelete() { /* Phase 4: confirm then remove from tree */ }

    internal static ProtocolType ResolveProtocolType(string protocol) =>
        protocol.ToUpperInvariant() switch
        {
            "SSH" or "SSH2" => ProtocolType.Ssh,
            "SFTP" => ProtocolType.SshSftp,
            "TELNET" => ProtocolType.Telnet,
            "RLOGIN" => ProtocolType.Rlogin,
            "RDP" => ProtocolType.Rdp,
            "VNC" => ProtocolType.Vnc,
            "HTTP" => ProtocolType.Http,
            "HTTPS" => ProtocolType.Https,
            "PS" or "POWERSHELL" => ProtocolType.PowerShell,
            "SERIAL" => ProtocolType.Serial,
            _ => ProtocolType.ExternalApp,
        };

    internal static int DefaultPortFor(ProtocolType type) => type switch
    {
        ProtocolType.Ssh or ProtocolType.SshSftp => 22,
        ProtocolType.Telnet => 23,
        ProtocolType.Rlogin => 513,
        ProtocolType.Rdp => 3389,
        ProtocolType.Vnc => 5900,
        ProtocolType.Http => 80,
        ProtocolType.Https => 443,
        _ => 22,
    };

    /// <summary>Default port from a protocol display string (used by dialogs).</summary>
    internal static int DefaultPortForString(string protocol) =>
        DefaultPortFor(ResolveProtocolType(protocol));

    internal static string ProtocolToString(ProtocolType type) => type switch
    {
        ProtocolType.Ssh => "SSH",
        ProtocolType.SshSftp => "SFTP",
        ProtocolType.Telnet => "Telnet",
        ProtocolType.Rlogin => "Rlogin",
        ProtocolType.Rdp => "RDP",
        ProtocolType.Vnc => "VNC",
        ProtocolType.Http => "HTTP",
        ProtocolType.Https => "HTTPS",
        ProtocolType.PowerShell => "PowerShell",
        ProtocolType.Serial => "Serial",
        _ => "ExternalApp",
    };

    /// <summary>Convert Core domain ProtocolType to display string.</summary>
    internal static string CoreProtocolToString(CoreProtocolType type) => type switch
    {
        CoreProtocolType.RDP => "RDP",
        CoreProtocolType.VNC => "VNC",
        CoreProtocolType.SSH1 => "SSH",
        CoreProtocolType.SSH2 => "SSH",
        CoreProtocolType.Telnet => "Telnet",
        CoreProtocolType.Rlogin => "Rlogin",
        CoreProtocolType.RAW => "RAW",
        CoreProtocolType.HTTP => "HTTP",
        CoreProtocolType.HTTPS => "HTTPS",
        CoreProtocolType.PowerShell => "PowerShell",
        _ => "SSH",
    };

    /// <summary>Convert display string to Core domain ProtocolType.</summary>
    internal static CoreProtocolType StringToCoreProtocol(string protocol) =>
        protocol.ToUpperInvariant() switch
        {
            "SSH" or "SSH2" => CoreProtocolType.SSH2,
            "SSH1" => CoreProtocolType.SSH1,
            "TELNET" => CoreProtocolType.Telnet,
            "RLOGIN" => CoreProtocolType.Rlogin,
            "RDP" => CoreProtocolType.RDP,
            "VNC" => CoreProtocolType.VNC,
            "HTTP" => CoreProtocolType.HTTP,
            "HTTPS" => CoreProtocolType.HTTPS,
            "RAW" => CoreProtocolType.RAW,
            "POWERSHELL" => CoreProtocolType.PowerShell,
            _ => CoreProtocolType.SSH2,
        };

    /// <summary>Create a VM node from a domain model ConnectionInfo.</summary>
    internal static ConnectionNodeViewModel FromModel(ConnectionInfo info)
    {
        var vm = new ConnectionNodeViewModel
        {
            Name = info.Name,
            IsFolder = info is ContainerInfo,
            IsExpanded = info is ContainerInfo c && c.IsExpanded,
            Protocol = CoreProtocolToString(info.Protocol),
            Hostname = info.Hostname,
            Port = info.Port,
            Username = info.Username,
            Model = info,
        };

        if (info is ContainerInfo container)
        {
            foreach (var child in container.Children)
                vm.Children.Add(FromModel(child));
        }

        return vm;
    }
}

/// <summary>
/// ViewModel for the connection tree panel.
/// Owns the root collection of connection nodes and tree-level commands.
/// </summary>
public sealed class ConnectionTreeViewModel : ReactiveObject
{
    private readonly ConnectionsService _connectionsService;
    private string _searchFilter = string.Empty;
    private ConnectionNodeViewModel? _selectedNode;
    private SessionsDockable? _sessionsDock;
    private IProtocolFactory? _protocolFactory;

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

    public ConnectionTreeViewModel(ConnectionsService connectionsService)
    {
        _connectionsService = connectionsService;

        var hasNonFolderSelection = this.WhenAnyValue(
            x => x.SelectedNode,
            n => n is { IsFolder: false })
            .ObserveOn(RxApp.MainThreadScheduler);

        ConnectSelectedCommand = ReactiveCommand.CreateFromTask(OnConnectSelectedAsync, hasNonFolderSelection);
        ConnectSelectedCommand.ThrownExceptions.Subscribe(ex =>
            System.Diagnostics.Trace.TraceError($"Connect error: {ex.Message}"));
        NewFolderCommand = ReactiveCommand.Create(OnNewFolder);
        NewConnectionCommand = ReactiveCommand.Create(OnNewConnection);
        DeleteSelectedCommand = ReactiveCommand.Create(OnDeleteSelected,
            this.WhenAnyValue(x => x.SelectedNode).Select(n => n != null).ObserveOn(RxApp.MainThreadScheduler));
        SortCommand = ReactiveCommand.Create(OnSort);

        // Start with an empty default tree
        CreateNewTree("Connections");
    }

    /// <summary>
    /// Inject the sessions dock and protocol factory (called from AppServices after DI build).
    /// </summary>
    public void SetDependencies(SessionsDockable sessionsDock, IProtocolFactory protocolFactory)
    {
        _sessionsDock = sessionsDock;
        _protocolFactory = protocolFactory;
        PropagateToNodes(Nodes);
    }

    /// <summary>Load connection tree from an XML file.</summary>
    public void LoadFromFile(string filePath, string password = "mR3m")
    {
        var model = _connectionsService.LoadFromFile(filePath, password);
        LoadFromModel(model);
    }

    /// <summary>Save the current tree to file.</summary>
    public void SaveToFile(string? filePath = null, string password = "mR3m")
    {
        SyncToModel();
        _connectionsService.SaveToFile(filePath, password);
    }

    /// <summary>Create a fresh empty tree.</summary>
    public void CreateNewTree(string name = "Connections")
    {
        var model = _connectionsService.CreateNew(name);
        LoadFromModel(model);
    }

    /// <summary>
    /// Add a new connection node to the tree. If a folder is selected, adds inside it;
    /// otherwise adds to the root.
    /// </summary>
    public ConnectionNodeViewModel AddConnection(string name, string protocol, string hostname, int port, string username = "")
    {
        var node = new ConnectionNodeViewModel
        {
            Name = name,
            Protocol = protocol,
            Hostname = hostname,
            Port = port,
            Username = username,
            IsFolder = false,
            SessionsDock = _sessionsDock,
            ProtocolFactory = _protocolFactory,
        };

        // Also create the domain model
        var info = new ConnectionInfo
        {
            Name = name,
            Protocol = ConnectionNodeViewModel.StringToCoreProtocol(protocol),
            Hostname = hostname,
            Port = port,
            Username = username,
        };
        node.Model = info;

        var target = GetInsertTarget();
        target.vm.Children.Add(node);
        target.container?.AddChild(info);

        return node;
    }

    private void LoadFromModel(ConnectionTreeModel model)
    {
        Nodes.Clear();
        var rootVm = ConnectionNodeViewModel.FromModel(model.RootNode);
        rootVm.IsExpanded = true;
        Nodes.Add(rootVm);
        PropagateToNodes(Nodes);
    }

    /// <summary>Sync ViewModel tree back to the domain model before saving.</summary>
    private void SyncToModel()
    {
        var model = _connectionsService.ConnectionTreeModel;
        if (model is null) return;

        // Rebuild children from ViewModel
        model.RootNode.Children.Clear();
        if (Nodes.Count > 0)
        {
            SyncChildren(Nodes[0], model.RootNode);
        }
    }

    private static void SyncChildren(ConnectionNodeViewModel vmNode, ContainerInfo container)
    {
        foreach (var childVm in vmNode.Children)
        {
            if (childVm.Model is not null)
            {
                // Update model from VM
                childVm.Model.Name = childVm.Name;
                childVm.Model.Hostname = childVm.Hostname;
                childVm.Model.Port = childVm.Port;
                childVm.Model.Username = childVm.Username;
                container.AddChild(childVm.Model);

                if (childVm.IsFolder && childVm.Model is ContainerInfo childContainer)
                {
                    childContainer.Children.Clear();
                    SyncChildren(childVm, childContainer);
                }
            }
            else
            {
                // New node without a model — create one
                ConnectionInfo info;
                if (childVm.IsFolder)
                {
                    var c = new ContainerInfo { Name = childVm.Name };
                    info = c;
                    SyncChildren(childVm, c);
                }
                else
                {
                    info = new ConnectionInfo
                    {
                        Name = childVm.Name,
                        Protocol = ConnectionNodeViewModel.StringToCoreProtocol(childVm.Protocol),
                        Hostname = childVm.Hostname,
                        Port = childVm.Port,
                        Username = childVm.Username,
                    };
                }
                childVm.Model = info;
                container.AddChild(info);
            }
        }
    }

    private void PropagateToNodes(IEnumerable<ConnectionNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            node.SessionsDock = _sessionsDock;
            node.ProtocolFactory = _protocolFactory;
            PropagateToNodes(node.Children);
        }
    }

    private (ConnectionNodeViewModel vm, ContainerInfo? container) GetInsertTarget()
    {
        if (SelectedNode is { IsFolder: true })
            return (SelectedNode, SelectedNode.Model as ContainerInfo);

        // Default to root
        if (Nodes.Count > 0)
            return (Nodes[0], Nodes[0].Model as ContainerInfo);

        return (new ConnectionNodeViewModel { Name = "Connections", IsFolder = true }, null);
    }

    private async Task OnConnectSelectedAsync()
    {
        if (SelectedNode is null || _sessionsDock is null || _protocolFactory is null) return;
        try
        {
            await SelectedNode.ConnectCommand.Execute().FirstAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Connect failed: {ex.Message}");
        }
    }

    private void OnNewFolder()
    {
        var folder = new ConnectionNodeViewModel
        {
            Name = "New Folder",
            IsFolder = true,
            IsExpanded = true,
            SessionsDock = _sessionsDock,
            ProtocolFactory = _protocolFactory,
        };

        var containerModel = new ContainerInfo { Name = "New Folder" };
        folder.Model = containerModel;

        var target = GetInsertTarget();
        target.vm.Children.Add(folder);
        target.container?.AddChild(containerModel);

        SelectedNode = folder;
    }

    private void OnNewConnection()
    {
        var node = AddConnection("New Connection", "SSH", "", 22);
        SelectedNode = node;
    }

    private void OnDeleteSelected()
    {
        if (SelectedNode is null) return;

        // Don't allow deleting the root node
        if (Nodes.Contains(SelectedNode)) return;

        // Find parent and remove
        RemoveNodeFromParent(Nodes, SelectedNode);
        SelectedNode = null;
    }

    private static bool RemoveNodeFromParent(
        ObservableCollection<ConnectionNodeViewModel> siblings,
        ConnectionNodeViewModel target)
    {
        if (siblings.Remove(target))
        {
            // Also remove from domain model
            if (target.Model?.Parent is ContainerInfo parent)
                parent.RemoveChild(target.Model);
            return true;
        }

        foreach (var sibling in siblings)
        {
            if (sibling.IsFolder && RemoveNodeFromParent(sibling.Children, target))
                return true;
        }

        return false;
    }

    private void OnSort()
    {
        var target = GetInsertTarget();
        SortChildren(target.vm.Children);
        (target.container)?.Sort();
    }

    private static void SortChildren(ObservableCollection<ConnectionNodeViewModel> children)
    {
        var sorted = children.OrderBy(c => !c.IsFolder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        children.Clear();
        foreach (var item in sorted)
            children.Add(item);
    }
}
