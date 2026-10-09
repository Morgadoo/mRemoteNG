using System.Collections.ObjectModel;
using System.DirectoryServices.Protocols;
using System.Reactive;
using System.Reactive.Linq;
using mRemoteNG.Core.Config.Import.ActiveDirectory;
using mRemoteNG.Core.Localization;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>An OU / container in the directory tree; children load when it is first expanded.</summary>
public sealed class DirectoryNodeViewModel : ReactiveObject
{
    private readonly Func<string, Task<IReadOnlyList<DirectoryContainer>>> _loadChildren;
    private bool _isExpanded;
    private bool _loaded;

    public DirectoryNodeViewModel(DirectoryContainer container, Func<string, Task<IReadOnlyList<DirectoryContainer>>> loadChildren)
        : this(container, loadChildren, isPlaceholder: false)
    {
    }

    private DirectoryNodeViewModel(DirectoryContainer container, Func<string, Task<IReadOnlyList<DirectoryContainer>>> loadChildren, bool isPlaceholder)
    {
        Container = container;
        _loadChildren = loadChildren;
        _loaded = isPlaceholder;
        // A placeholder child makes the node expandable until its real children are known.
        if (!isPlaceholder)
            Children.Add(new DirectoryNodeViewModel(new DirectoryContainer("", Localizer.Get("LoadingEllipsis"), false), loadChildren, isPlaceholder: true));
    }

    public DirectoryContainer Container { get; }
    public string Name => Container.Name;
    public string DistinguishedName => Container.DistinguishedName;
    public ObservableCollection<DirectoryNodeViewModel> Children { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            if (value) _ = LoadChildrenAsync();
        }
    }

    public async Task LoadChildrenAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var children = await _loadChildren(DistinguishedName);
        Children.Clear();
        foreach (var child in children)
            Children.Add(new DirectoryNodeViewModel(child, _loadChildren));
    }
}

/// <summary>A computer in the list, with its import check box.</summary>
public sealed class DirectoryComputerViewModel(DirectoryComputer computer) : ReactiveObject
{
    private bool _isSelected = true;

    public DirectoryComputer Computer { get; } = computer;
    public string Name => Computer.Name;
    public string HostName => Computer.HostName;
    public string OperatingSystem => Computer.OperatingSystem ?? "";
    public string Description => Computer.Description ?? "";
    public string Location => string.Join(" / ", ActiveDirectoryBrowser.SplitDn(Computer.DistinguishedName).Skip(1)
        .TakeWhile(rdn => !rdn.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
        .Select(ActiveDirectoryBrowser.RdnValue).Reverse());

    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

/// <summary>
/// Active Directory import (legacy Import ▸ From Active Directory): connect to a domain controller, browse the OU
/// tree, pick an OU (optionally with sub-OUs) and the computers to import as RDP connections.
/// </summary>
public sealed class ActiveDirectoryImportViewModel : ReactiveObject, IDisposable
{
    private readonly Func<LdapServerSettings, IDirectoryBrowser> _browserFactory;
    private IDirectoryBrowser? _browser;
    private string _server = "";
    private int _port;
    private bool _useSsl;
    private LdapBindMode _bindMode = OperatingSystem.IsWindows() ? LdapBindMode.Negotiate : LdapBindMode.Simple;
    private string _username = "";
    private string _password = "";
    private bool _includeSubOus = true;
    private bool _createOuFolders = true;
    private bool _isBusy;
    private bool _isConnected;
    private string _statusText = OperatingSystem.IsWindows()
        ? Localizer.Get("AdHintWindows")
        : Localizer.Get("AdHintOther");
    private DirectoryNodeViewModel? _selectedNode;
    private int _loadGeneration;

    public ActiveDirectoryImportViewModel(Func<LdapServerSettings, IDirectoryBrowser>? browserFactory = null)
    {
        _browserFactory = browserFactory ?? (settings => new ActiveDirectoryBrowser(settings));
        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync, this.WhenAnyValue(x => x.IsBusy, busy => !busy));
        SelectAllCommand = ReactiveCommand.Create(() => SetAll(true));
        SelectNoneCommand = ReactiveCommand.Create(() => SetAll(false));
        this.WhenAnyValue(x => x.SelectedNode, x => x.IncludeSubOus)
            .Skip(1)
            .Subscribe(_ => LoadComputersInBackground());
    }

    public string Server { get => _server; set => this.RaiseAndSetIfChanged(ref _server, value); }

    /// <summary>0 = default (389, or 636 with LDAPS).</summary>
    public int Port { get => _port; set => this.RaiseAndSetIfChanged(ref _port, value); }
    public bool UseSsl { get => _useSsl; set => this.RaiseAndSetIfChanged(ref _useSsl, value); }
    public IReadOnlyList<LdapBindMode> BindModes { get; } = Enum.GetValues<LdapBindMode>();
    public LdapBindMode BindMode { get => _bindMode; set => this.RaiseAndSetIfChanged(ref _bindMode, value); }
    public string Username { get => _username; set => this.RaiseAndSetIfChanged(ref _username, value); }
    public string Password { get => _password; set => this.RaiseAndSetIfChanged(ref _password, value); }
    public bool IncludeSubOus { get => _includeSubOus; set => this.RaiseAndSetIfChanged(ref _includeSubOus, value); }
    public bool CreateOuFolders { get => _createOuFolders; set => this.RaiseAndSetIfChanged(ref _createOuFolders, value); }
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }
    public bool IsConnected { get => _isConnected; private set => this.RaiseAndSetIfChanged(ref _isConnected, value); }
    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public ObservableCollection<DirectoryNodeViewModel> Nodes { get; } = [];
    public ObservableCollection<DirectoryComputerViewModel> Computers { get; } = [];

    public DirectoryNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set => this.RaiseAndSetIfChanged(ref _selectedNode, value);
    }

    public ReactiveCommand<Unit, Unit> ConnectCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectAllCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectNoneCommand { get; }

    private LdapServerSettings ServerSettings => new()
    {
        Server = Server.Trim(),
        Port = Port,
        UseSsl = UseSsl,
        BindMode = BindMode,
        Username = string.IsNullOrWhiteSpace(Username) ? null : Username.Trim(),
        Password = BindMode == LdapBindMode.Anonymous ? null : Password,
    };

    /// <summary>Binds and shows the domain root with its OUs.</summary>
    public async Task ConnectAsync()
    {
        IsBusy = true;
        StatusText = Localizer.Get("ConnectingEllipsis");
        _browser?.Dispose();
        _browser = null;
        Nodes.Clear();
        Computers.Clear();
        IsConnected = false;
        try
        {
            var settings = ServerSettings;
            var browser = _browserFactory(settings);
            var root = await Task.Run(() =>
            {
                browser.Bind();
                return browser.GetDefaultNamingContext();
            });
            _browser = browser;
            var rootNode = new DirectoryNodeViewModel(new DirectoryContainer(root, root, false), LoadChildrenAsync);
            Nodes.Add(rootNode);
            IsConnected = true;
            await rootNode.LoadChildrenAsync();
            rootNode.IsExpanded = true;
            SelectedNode = rootNode;
            StatusText = string.IsNullOrEmpty(settings.Server)
                ? Localizer.Get("AdConnectedToDomain")
                : Localizer.Format("AdConnectedToServerFormat", settings.Server);
        }
        catch (Exception ex)
        {
            StatusText = Describe(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<IReadOnlyList<DirectoryContainer>> LoadChildrenAsync(string dn)
    {
        var browser = _browser;
        if (browser is null) return [];
        try
        {
            return await Task.Run(() => browser.GetChildContainers(dn));
        }
        catch (Exception ex)
        {
            StatusText = Describe(ex);
            return [];
        }
    }

    private void LoadComputersInBackground() => _ = LoadComputersAsync();

    /// <summary>Lists the computers of the selected node (also called automatically when the selection changes).</summary>
    public async Task LoadComputersAsync()
    {
        var browser = _browser;
        var node = SelectedNode;
        var generation = ++_loadGeneration;
        Computers.Clear();
        if (browser is null || node is null || node.DistinguishedName.Length == 0) return;

        StatusText = Localizer.Format("AdReadingComputersFormat", node.Name);
        try
        {
            var subtree = IncludeSubOus;
            var computers = await Task.Run(() => browser.GetComputers(node.DistinguishedName, subtree));
            if (generation != _loadGeneration) return;
            foreach (var computer in computers)
                Computers.Add(new DirectoryComputerViewModel(computer));
            StatusText = computers.Count == 0
                ? Localizer.Format(subtree ? "AdNoComputersSubtreeFormat" : "AdNoComputersFormat", node.Name)
                : Localizer.Format(subtree ? "AdComputersSubtreeFormat" : "AdComputersFormat", computers.Count, node.Name);
        }
        catch (Exception ex)
        {
            if (generation == _loadGeneration) StatusText = Describe(ex);
        }
    }

    private void SetAll(bool selected)
    {
        foreach (var computer in Computers) computer.IsSelected = selected;
    }

    /// <summary>The import to run, or null (with a status message) when nothing is chosen.</summary>
    public ActiveDirectoryImportRequest? BuildRequest()
    {
        if (!IsConnected || SelectedNode is null)
        {
            StatusText = Localizer.Get("AdConnectFirst");
            return null;
        }
        var selected = Computers.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            StatusText = Localizer.Get("AdSelectComputer");
            return null;
        }
        return new ActiveDirectoryImportRequest
        {
            Server = ServerSettings,
            BaseDn = SelectedNode.DistinguishedName,
            IncludeSubOus = IncludeSubOus,
            CreateOuFolders = CreateOuFolders,
            // Every computer checked means "the whole OU", which also picks up computers added later.
            SelectedComputers = selected.Count == Computers.Count ? [] : selected.Select(c => c.Computer.DistinguishedName).ToList(),
        };
    }

    private static string Describe(Exception ex) => ex switch
    {
        LdapException { ErrorCode: 49 } => Localizer.Get("AdInvalidCredentials"),
        LdapException { ErrorCode: 81 } => Localizer.Get("AdServerUnavailable"),
        LdapException ldap => Localizer.Format("AdLdapErrorFormat", ldap.ErrorCode, ldap.Message),
        DllNotFoundException => Localizer.Get("AdLdapLibraryMissing"),
        _ => ex.Message,
    };

    public void Dispose() => _browser?.Dispose();
}
