using System.Collections.ObjectModel;
using mRemoteNG.Avalonia.ViewModels.Docking;
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
        var canConnect = this.WhenAnyValue(x => x.IsFolder, isFolder => !isFolder);
        ConnectCommand = ReactiveCommand.CreateFromTask(OnConnectAsync, canConnect);
        EditCommand = ReactiveCommand.Create(OnEdit);
        DeleteCommand = ReactiveCommand.Create(OnDelete);
    }

    private async Task OnConnectAsync()
    {
        if (IsFolder || SessionsDock is null || ProtocolFactory is null) return;

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

    private void OnEdit() { /* Phase 4: open ConnectionDialog pre-filled with this node */ }
    private void OnDelete() { /* Phase 4: confirm then remove from tree */ }

    private static ProtocolType ResolveProtocolType(string protocol) =>
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

    private static int DefaultPortFor(ProtocolType type) => type switch
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
}

/// <summary>
/// ViewModel for the connection tree panel.
/// Owns the root collection of connection nodes and tree-level commands.
/// </summary>
public sealed class ConnectionTreeViewModel : ReactiveObject
{
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

    public ConnectionTreeViewModel()
    {
        var hasNonFolderSelection = this.WhenAnyValue(
            x => x.SelectedNode,
            n => n is { IsFolder: false });

        ConnectSelectedCommand = ReactiveCommand.CreateFromTask(OnConnectSelectedAsync, hasNonFolderSelection);
        NewFolderCommand = ReactiveCommand.Create(OnNewFolder);
        NewConnectionCommand = ReactiveCommand.Create(OnNewConnection);
        DeleteSelectedCommand = ReactiveCommand.Create(OnDeleteSelected, this.WhenAnyValue(x => x.SelectedNode, n => n != null));
        SortCommand = ReactiveCommand.Create(OnSort);

        LoadDemoData();
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

    private void PropagateToNodes(IEnumerable<ConnectionNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            node.SessionsDock = _sessionsDock;
            node.ProtocolFactory = _protocolFactory;
            PropagateToNodes(node.Children);
        }
    }

    private void LoadDemoData()
    {
        var root = new ConnectionNodeViewModel { Name = "Connections", IsFolder = true, IsExpanded = true };

        var servers = new ConnectionNodeViewModel { Name = "Linux Servers", IsFolder = true, IsExpanded = true };
        servers.Children.Add(new ConnectionNodeViewModel
            { Name = "Web Server 01", Protocol = "SSH", Hostname = "192.168.1.10", Port = 22, Username = "admin" });
        servers.Children.Add(new ConnectionNodeViewModel
            { Name = "Monitoring Host", Protocol = "SSH", Hostname = "192.168.1.15", Port = 22 });

        var windows = new ConnectionNodeViewModel { Name = "Windows Servers", IsFolder = true };
        windows.Children.Add(new ConnectionNodeViewModel
            { Name = "DC01", Protocol = "RDP", Hostname = "192.168.1.100", Port = 3389 });
        windows.Children.Add(new ConnectionNodeViewModel
            { Name = "SQL Server", Protocol = "RDP", Hostname = "192.168.1.101", Port = 3389 });

        var network = new ConnectionNodeViewModel { Name = "Network Devices", IsFolder = true };
        network.Children.Add(new ConnectionNodeViewModel
            { Name = "Core Switch", Protocol = "Telnet", Hostname = "10.0.0.1", Port = 23 });
        network.Children.Add(new ConnectionNodeViewModel
            { Name = "Router", Protocol = "SSH", Hostname = "10.0.0.254", Port = 22 });

        root.Children.Add(servers);
        root.Children.Add(windows);
        root.Children.Add(network);
        Nodes.Add(root);
    }

    private async Task OnConnectSelectedAsync()
    {
        if (SelectedNode is null || _sessionsDock is null || _protocolFactory is null) return;
        await SelectedNode.ConnectCommand.Execute();
    }

    private void OnNewFolder() { /* Phase 4: add folder node to tree */ }
    private void OnNewConnection() { /* Phase 4: open ConnectionDialog then add to tree */ }
    private void OnDeleteSelected() { /* Phase 4: confirm + remove */ }
    private void OnSort() { /* Phase 4: sort children alphabetically */ }
}
