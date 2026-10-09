using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Config.Putty;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Net;
using mRemoteNG.Core.Settings;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;
using mRemoteNG.Platform;
using mRemoteNG.Protocols.Abstractions;
using ReactiveUI;
using CoreProtocolType = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>
/// Thin live wrapper over a Core <see cref="ConnectionInfo"/> node. The model is the source of truth:
/// display properties read straight from it, and model changes (property and child-collection
/// notifications) are reflected here automatically.
/// </summary>
public sealed class ConnectionNodeViewModel : ReactiveObject, IDisposable
{
    private static readonly string[] DisplayProperties =
    [
        nameof(Name), nameof(Hostname), nameof(Port), nameof(Username), nameof(Description),
        nameof(Protocol), nameof(ProtocolLabel), nameof(IconImage), nameof(ToolTip), nameof(IsSupported),
    ];

    private readonly ConnectionTreeViewModel _tree;
    private bool _isVisible = true;
    private bool _isCut;
    private bool _isEditing;
    private string _editName = string.Empty;
    private bool _disposed;

    internal ConnectionNodeViewModel(ConnectionInfo model, ConnectionNodeViewModel? parent, ConnectionTreeViewModel tree)
    {
        Model = model;
        Parent = parent;
        _tree = tree;

        Model.PropertyChanged += OnModelPropertyChanged;
        if (Model is ContainerInfo container)
        {
            container.CollectionChanged += OnModelCollectionChanged;
            SyncChildren();
        }
    }

    /// <summary>The Core node this view model wraps.</summary>
    public ConnectionInfo Model { get; }

    public ConnectionNodeViewModel? Parent { get; }

    public ObservableCollection<ConnectionNodeViewModel> Children { get; } = [];

    public string Name
    {
        get => Model.Name;
        set => Model.Name = value;
    }

    public string Hostname => Model.Hostname;
    public int Port => Model.Port;
    public string Username => Model.Username;
    public string Description => Model.Description;
    public CoreProtocolType Protocol => Model.Protocol;

    public bool IsFolder => Model is ContainerInfo;
    public bool IsRoot => Model is RootNodeInfo;

    /// <summary>The "PuTTY Sessions" root.</summary>
    public bool IsPuttyRoot => Model is RootPuttySessionsNodeInfo;

    /// <summary>A PuTTY saved session (read-only; can be connected or copied into the tree).</summary>
    public bool IsPuttySession => Model is PuttySessionNodeInfo;

    /// <summary>True for nodes that do not belong to the connection file (PuTTY sessions and their root).</summary>
    public bool IsReadOnly => IsPuttyRoot || IsPuttySession;

    /// <summary>Protocol badge text (empty for folders).</summary>
    public string ProtocolLabel => IsFolder ? string.Empty : Model.Protocol.ToString();

    /// <summary>False for connections whose protocol has no cross-platform implementation.</summary>
    public bool IsSupported => IsFolder || ConnectionParametersFactory.MapProtocol(Model.Protocol) is not null;

    public Bitmap? IconImage => IsFolder
        ? null
        : IconService.LoadIcon($"{Model.Icon}.ico") ?? IconService.GetProtocolIcon(Model.Protocol.ToString());

    public string ToolTip
    {
        get
        {
            if (IsPuttyRoot)
                return "Saved PuTTY sessions (read-only). Connect, or duplicate a session to copy it into the connection tree.";
            if (IsFolder)
                return string.IsNullOrEmpty(Description) ? Name : $"{Name}\n{Description}";
            var host = string.IsNullOrEmpty(Hostname) ? "(no hostname)" : Hostname;
            var target = Port > 0 ? $"{host}:{Port}" : host;
            var text = $"{Model.Protocol}  {target}";
            if (!string.IsNullOrEmpty(Username)) text += $"\nUser: {Username}";
            if (!string.IsNullOrEmpty(Description)) text += $"\n{Description}";
            if (IsPuttySession) text += "\nPuTTY saved session";
            if (!IsSupported) text += $"\n{Model.Protocol} is not supported on this platform yet.";
            return text;
        }
    }

    public bool IsExpanded
    {
        get => Model is ContainerInfo { IsExpanded: true };
        set
        {
            if (Model is ContainerInfo container && container.IsExpanded != value)
                container.IsExpanded = value; // raises PropertyChanged → OnModelPropertyChanged
        }
    }

    /// <summary>False when hidden by the search filter.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    /// <summary>True while the node is cut and waiting to be pasted.</summary>
    public bool IsCut
    {
        get => _isCut;
        set
        {
            this.RaiseAndSetIfChanged(ref _isCut, value);
            this.RaisePropertyChanged(nameof(DisplayOpacity));
        }
    }

    public double DisplayOpacity => IsCut ? 0.5 : 1.0;

    /// <summary>True while the name is being edited in place (F2).</summary>
    public bool IsEditing
    {
        get => _isEditing;
        internal set => this.RaiseAndSetIfChanged(ref _isEditing, value);
    }

    /// <summary>The name typed in the in-place editor.</summary>
    public string EditName
    {
        get => _editName;
        set => this.RaiseAndSetIfChanged(ref _editName, value ?? string.Empty);
    }

    /// <summary>This node and all descendants, depth first.</summary>
    public IEnumerable<ConnectionNodeViewModel> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var node in child.SelfAndDescendants())
                yield return node;
        }
    }

    /// <summary>Re-reads every display property (e.g. after a folder's inherited values changed).</summary>
    internal void RefreshDisplay()
    {
        foreach (var property in DisplayProperties)
            this.RaisePropertyChanged(property);
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Containers bubble their descendants' notifications; only react to this node's own.
        if (!ReferenceEquals(sender, Model)) return;

        if (e.PropertyName == nameof(ContainerInfo.IsExpanded))
        {
            this.RaisePropertyChanged(nameof(IsExpanded));
            return;
        }

        RefreshDisplay();

        // Children may inherit the changed value.
        foreach (var descendant in SelfAndDescendants().Skip(1))
            descendant.RefreshDisplay();
    }

    private void OnModelCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, Model)) return;
        SyncChildren();
        _tree.OnStructureChanged();
    }

    /// <summary>Mirrors the container's child list, reusing existing view models so state is kept.</summary>
    private void SyncChildren()
    {
        if (Model is not ContainerInfo container) return;
        var models = container.Children;

        for (var i = 0; i < models.Count; i++)
        {
            var existing = -1;
            for (var j = i; j < Children.Count; j++)
            {
                if (ReferenceEquals(Children[j].Model, models[i]))
                {
                    existing = j;
                    break;
                }
            }

            if (existing == i) continue;
            if (existing > i)
                Children.Move(existing, i);
            else
                Children.Insert(i, new ConnectionNodeViewModel(models[i], this, _tree));
        }

        while (Children.Count > models.Count)
        {
            var removed = Children[^1];
            Children.RemoveAt(Children.Count - 1);
            removed.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Model.PropertyChanged -= OnModelPropertyChanged;
        if (Model is ContainerInfo container)
            container.CollectionChanged -= OnModelCollectionChanged;
        foreach (var child in Children)
            child.Dispose();
    }
}

/// <summary>Where a dragged node lands relative to the drop target.</summary>
public enum TreeDropPosition
{
    /// <summary>Into the target folder (appended).</summary>
    Into,
    /// <summary>Into the target's folder, above the target.</summary>
    Before,
}

/// <summary>A yes/no question with custom button labels.</summary>
public sealed record TreeQuestion(string Title, string Message, string YesLabel, string NoLabel = "Cancel");

/// <summary>
/// ViewModel for the connection tree panel. Wraps the Core <see cref="ConnectionTreeModel"/> held by
/// <see cref="ConnectionsService"/>; every edit is made on the Core tree and the view models follow.
/// A second, read-only "PuTTY Sessions" root lists the user's saved PuTTY sessions when there are any.
/// </summary>
public sealed class ConnectionTreeViewModel : ReactiveObject
{
    private readonly ConnectionsService _connectionsService;
    private readonly AppSettingsService? _settings;
    private readonly PuttySessionsTree? _puttySessionsTree;
    private ConnectionTreeChangeTracker? _tracker;
    private string _searchFilter = string.Empty;
    private Dictionary<ContainerInfo, bool>? _expansionBeforeSearch;
    private bool _filterRefreshPending;
    private ConnectionNodeViewModel? _selectedNode;
    private ConnectionInfo? _cutNode;
    private bool _isDirty;
    private SessionsDockable? _sessionsDock;
    private IProtocolFactory? _protocolFactory;
    private ConnectionInfo? _defaultConnection;
    private ConnectionNodeViewModel? _puttyRootNode;

    public ConnectionTreeViewModel(ConnectionsService connectionsService)
        : this(connectionsService, null, null)
    {
    }

    /// <param name="settings">Where the default connection is stored (null: kept in memory only).</param>
    /// <param name="puttySessionsProvider">Source of the "PuTTY Sessions" root (null: no PuTTY root).</param>
    public ConnectionTreeViewModel(ConnectionsService connectionsService, AppSettingsService? settings,
        PuttySessionsTree? puttySessionsTree)
    {
        _connectionsService = connectionsService;
        _settings = settings;
        _puttySessionsTree = puttySessionsTree;

        var selection = this.WhenAnyValue(x => x.SelectedNode).ObserveOn(RxApp.MainThreadScheduler);
        var hasConnection = selection.Select(n => n is { IsFolder: false });
        var hasNode = selection.Select(n => n is not null);
        var canConnect = selection.Select(n => n is { IsFolder: false } or { IsFolder: true, IsRoot: false });
        var editable = selection.Select(n => n is { IsReadOnly: false });
        var hasNonRoot = selection.Select(n => n is { IsRoot: false, IsReadOnly: false });
        var canDuplicate = selection.Select(n => n is { IsRoot: false });
        var hasFolder = selection.Select(n => n is { IsFolder: true, IsReadOnly: false });
        var hasHostname = selection.Select(n => n is { IsFolder: false } && !string.IsNullOrWhiteSpace(n.Hostname));

        ConnectSelectedCommand = ReactiveCommand.CreateFromTask(() => ConnectSelectedAsync(null), canConnect);
        ConnectWithOptionsCommand = ReactiveCommand.CreateFromTask(() => ConnectWithOptionsAsync(focusPanel: false), canConnect);
        ConnectChoosePanelCommand = ReactiveCommand.CreateFromTask(() => ConnectWithOptionsAsync(focusPanel: true), canConnect);
        ConnectWithPresetCommand = ReactiveCommand.CreateFromTask<ConnectPreset>(
            preset => ConnectSelectedAsync(ConnectWithOptionsViewModel.ForPreset(preset)), canConnect);
        DisconnectCommand = ReactiveCommand.CreateFromTask(DisconnectSelectedAsync, hasNode);
        NewFolderCommand = ReactiveCommand.CreateFromTask(NewFolderAsync);
        NewConnectionCommand = ReactiveCommand.CreateFromTask(NewConnectionAsync);
        EditSelectedCommand = ReactiveCommand.CreateFromTask(EditSelectedAsync, editable);
        RenameSelectedCommand = ReactiveCommand.Create(() => { if (SelectedNode is { } n) BeginRename(n); }, editable);
        DuplicateSelectedCommand = ReactiveCommand.Create(DuplicateSelected, canDuplicate);
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync, hasNonRoot);
        CopyHostnameCommand = ReactiveCommand.CreateFromTask(CopyHostnameAsync, hasHostname);
        SortCommand = ReactiveCommand.Create(() => SortSelected(ListSortDirection.Ascending));
        SortAscendingCommand = ReactiveCommand.Create(() => SortSelected(ListSortDirection.Ascending));
        SortDescendingCommand = ReactiveCommand.Create(() => SortSelected(ListSortDirection.Descending));
        ExpandAllCommand = ReactiveCommand.Create(() => SetExpandedAll(true));
        CollapseAllCommand = ReactiveCommand.Create(() => SetExpandedAll(false));
        MoveUpCommand = ReactiveCommand.Create(() => MoveSelected(up: true), hasNonRoot);
        MoveDownCommand = ReactiveCommand.Create(() => MoveSelected(up: false), hasNonRoot);
        CutCommand = ReactiveCommand.Create(CutSelected, hasNonRoot);
        PasteCommand = ReactiveCommand.Create(PasteIntoSelection,
            this.WhenAnyValue(x => x.HasCutNode).ObserveOn(RxApp.MainThreadScheduler));
        ApplyInheritanceToChildrenCommand = ReactiveCommand.Create(ApplyInheritanceToChildren, hasFolder);
        ApplyDefaultInheritanceCommand = ReactiveCommand.Create(ApplyDefaultInheritance, hasNonRoot);
        EditDefaultConnectionCommand = ReactiveCommand.CreateFromTask(EditDefaultConnectionAsync);
        FilePropertiesCommand = ReactiveCommand.CreateFromTask(EditFilePropertiesAsync);
        WakeOnLanCommand = ReactiveCommand.CreateFromTask(WakeSelectedAsync, canConnect);
        RefreshPuttySessionsCommand = ReactiveCommand.CreateFromTask(RefreshPuttySessionsAsync);
        ClearSearchCommand = ReactiveCommand.Create(() => { SearchFilter = string.Empty; });

        foreach (var command in new IHandleObservableErrors[]
                 {
                     ConnectSelectedCommand, ConnectWithOptionsCommand, ConnectChoosePanelCommand, ConnectWithPresetCommand,
                     DisconnectCommand, NewFolderCommand, NewConnectionCommand, EditSelectedCommand, RenameSelectedCommand,
                     DuplicateSelectedCommand, DeleteSelectedCommand, CopyHostnameCommand, SortCommand, SortAscendingCommand,
                     SortDescendingCommand, ExpandAllCommand, CollapseAllCommand, MoveUpCommand, MoveDownCommand, CutCommand,
                     PasteCommand, ApplyInheritanceToChildrenCommand, ApplyDefaultInheritanceCommand,
                     EditDefaultConnectionCommand, FilePropertiesCommand, WakeOnLanCommand, RefreshPuttySessionsCommand,
                 })
        {
            command.ThrownExceptions.Subscribe(ex => Log($"Connection tree: {ex.Message}", LogLevel.Error));
        }

        CreateNewTree();
        if (_puttySessionsTree is not null)
            RefreshPuttySessionsCommand.Execute().Subscribe(_ => { }, _ => { });
    }

    public ObservableCollection<ConnectionNodeViewModel> Nodes { get; } = [];

    /// <summary>The Core root node currently shown.</summary>
    public RootNodeInfo? Root => _connectionsService.ConnectionTreeModel?.RootNode;

    /// <summary>The "PuTTY Sessions" root, or null when there are no saved PuTTY sessions.</summary>
    public ConnectionNodeViewModel? PuttyRootNode => _puttyRootNode;

    public ConnectionNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set => this.RaiseAndSetIfChanged(ref _selectedNode, value);
    }

    public string SearchFilter
    {
        get => _searchFilter;
        set
        {
            if (_searchFilter == (value ?? string.Empty)) return;
            this.RaiseAndSetIfChanged(ref _searchFilter, value ?? string.Empty);
            ApplyFilter();
        }
    }

    /// <summary>True when the tree has changes that are not saved to a file.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => this.RaiseAndSetIfChanged(ref _isDirty, value);
    }

    /// <summary>Path of the loaded/saved file, or null for a new unsaved tree.</summary>
    public string? CurrentFilePath => _connectionsService.CurrentFilePath;

    /// <summary>"MySQL host/db (read-only)" when the tree was loaded from a SQL database, else null.</summary>
    public string? DatabaseName => _connectionsService.Database is { } db
        ? db.DisplayName + (db.ReadOnly ? " (read-only)" : string.Empty)
        : null;

    public bool HasCutNode => _cutNode is not null;

    /// <summary>
    /// The default connection: values and inheritance flags given to new connections and folders
    /// (legacy "default connection properties" / "default inheritance").
    /// </summary>
    public ConnectionInfo DefaultConnection => _defaultConnection ??= LoadDefaultConnection();

    /// <summary>Re-reads the default connection from the settings (after they changed elsewhere).</summary>
    public void ReloadDefaultConnection() => _defaultConnection = null;

    // ── Interactions (handled by the views) ───────────────────────────────

    /// <summary>Shows the connection editor; returns true when the user pressed OK.</summary>
    public Interaction<ConnectionDialogViewModel, bool> EditNode { get; } = new();

    /// <summary>Asks a yes/no question (title, message) with Delete/Cancel buttons; returns true for yes.</summary>
    public Interaction<(string Title, string Message), bool> Confirm { get; } = new();

    /// <summary>Asks a yes/no question with custom buttons; returns true for yes.</summary>
    public Interaction<TreeQuestion, bool> Ask { get; } = new();

    /// <summary>Shows "Connect with options"; returns true to connect.</summary>
    public Interaction<ConnectWithOptionsViewModel, bool> ChooseConnectOptions { get; } = new();

    /// <summary>Shows File ▸ Properties; returns true when the user pressed OK.</summary>
    public Interaction<FilePropertiesViewModel, bool> EditFileProperties { get; } = new();

    /// <summary>Puts text on the clipboard.</summary>
    public Interaction<string, Unit> CopyToClipboard { get; } = new();

    // ── Commands ──────────────────────────────────────────────────────────

    /// <summary>Connects the selected connection, or every connection in the selected folder.</summary>
    public ReactiveCommand<Unit, Unit> ConnectSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectWithOptionsCommand { get; }
    public ReactiveCommand<Unit, Unit> ConnectChoosePanelCommand { get; }

    /// <summary>The legacy "Connect (with options)" sub-menu entries; the parameter is a <see cref="ConnectPreset"/>.</summary>
    public ReactiveCommand<ConnectPreset, Unit> ConnectWithPresetCommand { get; }

    /// <summary>Closes the sessions opened from the selected connection (or any connection in the selected folder).</summary>
    public ReactiveCommand<Unit, Unit> DisconnectCommand { get; }
    public ReactiveCommand<Unit, Unit> NewFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> NewConnectionCommand { get; }

    /// <summary>Opens the property editor (File ▸ Properties for the root).</summary>
    public ReactiveCommand<Unit, Unit> EditSelectedCommand { get; }

    /// <summary>Starts renaming the selected node in place (F2).</summary>
    public ReactiveCommand<Unit, Unit> RenameSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> DuplicateSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyHostnameCommand { get; }

    /// <summary>Sorts the selected folder (or the selected connection's folder) and its sub-folders A–Z.</summary>
    public ReactiveCommand<Unit, Unit> SortCommand { get; }
    public ReactiveCommand<Unit, Unit> SortAscendingCommand { get; }
    public ReactiveCommand<Unit, Unit> SortDescendingCommand { get; }
    public ReactiveCommand<Unit, Unit> ExpandAllCommand { get; }
    public ReactiveCommand<Unit, Unit> CollapseAllCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
    public ReactiveCommand<Unit, Unit> CutCommand { get; }
    public ReactiveCommand<Unit, Unit> PasteCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyInheritanceToChildrenCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyDefaultInheritanceCommand { get; }

    /// <summary>Edits the default connection properties and default inheritance.</summary>
    public ReactiveCommand<Unit, Unit> EditDefaultConnectionCommand { get; }

    /// <summary>File ▸ Properties: root name, master password and encryption settings of the open file.</summary>
    public ReactiveCommand<Unit, Unit> FilePropertiesCommand { get; }

    /// <summary>Sends a Wake-on-LAN packet to the selected connection (or every connection in the folder) with a MAC address.</summary>
    public ReactiveCommand<Unit, Unit> WakeOnLanCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshPuttySessionsCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearSearchCommand { get; }

    /// <summary>Inject the sessions dock and protocol factory (called from AppServices after DI build).</summary>
    public void SetDependencies(SessionsDockable sessionsDock, IProtocolFactory protocolFactory)
    {
        _sessionsDock = sessionsDock;
        _protocolFactory = protocolFactory;
    }

    // ── File operations ───────────────────────────────────────────────────

    /// <summary>Load connection tree from an XML file.</summary>
    /// <param name="password">Master password, or null to use the default key.</param>
    /// <exception cref="mRemoteNG.Core.Config.Serializers.Xml.ConnectionFilePasswordException">
    /// The file is protected and <paramref name="password"/> is missing or wrong.
    /// </exception>
    public void LoadFromFile(string filePath, string? password = null)
    {
        var model = _connectionsService.LoadFromFile(filePath, password);
        LoadFromModel(model);
    }

    /// <summary>Save the current tree to file, keeping the file's master password.</summary>
    public void SaveToFile(string? filePath = null)
    {
        _connectionsService.SaveToFile(filePath);
        _tracker?.MarkClean();
        this.RaisePropertyChanged(nameof(CurrentFilePath));
    }

    /// <summary>Create a fresh empty tree (not dirty, no file).</summary>
    public void CreateNewTree(string name = "Connections")
    {
        var model = _connectionsService.CreateNew(name);
        LoadFromModel(model);
    }

    private void LoadFromModel(ConnectionTreeModel model)
    {
        _tracker?.Dispose();
        foreach (var node in Nodes.Where(n => !n.IsPuttyRoot))
            node.Dispose();
        Nodes.Clear();
        _cutNode = null;
        this.RaisePropertyChanged(nameof(HasCutNode));
        this.RaisePropertyChanged(nameof(CurrentFilePath));
        this.RaisePropertyChanged(nameof(DatabaseName));
        _expansionBeforeSearch = null;

        model.RootNode.IsExpanded = true;
        var rootVm = new ConnectionNodeViewModel(model.RootNode, null, this);
        Nodes.Add(rootVm);
        if (_puttyRootNode is not null)
            Nodes.Add(_puttyRootNode);
        SelectedNode = rootVm;

        _tracker = new ConnectionTreeChangeTracker(model.RootNode);
        _tracker.DirtyChanged += (_, _) => IsDirty = _tracker.IsDirty;
        IsDirty = false;
        this.RaisePropertyChanged(nameof(CurrentFilePath));
        this.RaisePropertyChanged(nameof(Root));

        if (!string.IsNullOrWhiteSpace(SearchFilter))
            ApplyFilter();
    }

    /// <summary>
    /// Re-reads the view from the Core model after external changes (e.g. an import). Nodes added to,
    /// removed from or moved within the Core tree through <see cref="ContainerInfo"/> methods already show
    /// up automatically; this also covers edits that raise no notifications and a model replaced in
    /// <see cref="ConnectionsService"/>. Selection and the search filter are kept; the dirty flag is not reset.
    /// </summary>
    public void RefreshFromModel()
    {
        var model = _connectionsService.ConnectionTreeModel;
        if (model is null) return;

        if (Nodes.Count == 0 || !ReferenceEquals(Nodes[0].Model, model.RootNode))
        {
            // A different tree: load it, but keep it marked as unsaved.
            var wasDirty = IsDirty;
            LoadFromModel(model);
            if (wasDirty) MarkDirty();
            return;
        }

        var selected = SelectedNode?.Model;
        var oldRoot = Nodes[0];
        var newRoot = new ConnectionNodeViewModel(model.RootNode, null, this);
        Nodes[0] = newRoot;
        oldRoot.Dispose();
        SelectedNode = (selected is null ? null : FindNode(selected)) ?? newRoot;

        if (!string.IsNullOrWhiteSpace(SearchFilter))
            ApplyFilter();
    }

    /// <summary>Marks the tree as having unsaved changes (for edits made outside this view model).</summary>
    public void MarkDirty() => _tracker?.MarkDirty();

    // ── Adding nodes ──────────────────────────────────────────────────────

    /// <summary>
    /// Adds <paramref name="info"/> to <paramref name="parent"/>, or to the insert target derived from the
    /// selection (selected folder, the selected connection's folder, else the root).
    /// </summary>
    public ConnectionNodeViewModel? AddConnection(ConnectionInfo info, ContainerInfo? parent = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        var (container, after) = parent is null ? GetInsertTarget() : (parent, null);
        if (after is not null)
            container.AddChildBelow(info, after);
        else
            container.AddChild(info);

        container.IsExpanded = true;
        var vm = FindNode(info);
        if (vm is not null)
            SelectedNode = vm;
        return vm;
    }

    /// <summary>Convenience overload: adds a connection with the given basics at the insert target.</summary>
    public ConnectionNodeViewModel? AddConnection(string name, CoreProtocolType protocol, string hostname, int port = 0, string username = "")
    {
        var info = ConnectionDefaults.ApplyNewConnectionDefaults(new ConnectionInfo());
        info.Name = name;
        info.Protocol = protocol;
        info.Hostname = hostname;
        info.Port = port > 0 ? port : ConnectionInfo.GetDefaultPort(protocol);
        info.Username = username;
        return AddConnection(info);
    }

    /// <summary>Finds the view model wrapping <paramref name="model"/>.</summary>
    public ConnectionNodeViewModel? FindNode(ConnectionInfo model) =>
        Nodes.SelectMany(n => n.SelfAndDescendants()).FirstOrDefault(n => ReferenceEquals(n.Model, model));

    /// <summary>A new connection (or folder) with the default connection's values and inheritance.</summary>
    public T CreateFromDefaults<T>(T node) where T : ConnectionInfo
    {
        DefaultConnectionSettings.ApplyTo(DefaultConnection, node);
        return node;
    }

    /// <summary>
    /// The folder new nodes go into: the selected folder, else the selected connection's folder
    /// (returned as <c>after</c> so the new node lands below it), else the root. PuTTY sessions are not
    /// part of the connection file, so selecting one targets the root.
    /// </summary>
    private (ContainerInfo container, ConnectionInfo? after) GetInsertTarget()
    {
        var root = Root ?? throw new InvalidOperationException("No connection tree loaded.");
        if (SelectedNode is { IsReadOnly: true })
            return (root, null);
        return SelectedNode?.Model switch
        {
            ContainerInfo folder => (folder, null),
            { Parent: { } parent } connection => (parent, connection),
            _ => (root, null),
        };
    }

    // ── Connect / disconnect ──────────────────────────────────────────────

    private async Task ConnectSelectedAsync(ConnectOptions? options)
    {
        if (SelectedNode is not { } node) return;
        var targets = ConnectionTreeOperations.ConnectionsToOpen(node.Model);
        if (targets.Count == 0)
        {
            Log($"\"{node.Name}\" contains no connections.", LogLevel.Warning);
            return;
        }

        foreach (var target in targets)
            await ConnectAsync(target, options);
    }

    /// <summary>Opens a session for <paramref name="node"/> using its resolved (inherited) settings.</summary>
    public Task ConnectAsync(ConnectionNodeViewModel node) =>
        node.IsFolder ? Task.CompletedTask : ConnectAsync(node.Model, null);

    /// <summary>Opens a session for <paramref name="connection"/> with optional one-off options.</summary>
    public async Task ConnectAsync(ConnectionInfo connection, ConnectOptions? options)
    {
        if (connection is ContainerInfo || _sessionsDock is null || _protocolFactory is null) return;

        try
        {
            // Preparation (credential providers, tunnels, …) and errors such as an unsupported
            // protocol are handled and reported by the sessions dock.
            await _sessionsDock.OpenConnectionAsync(connection, _protocolFactory, options);
        }
        catch (Exception ex)
        {
            _sessionsDock.ReportError($"Could not connect to \"{connection.Name}\": {ex.Message}");
        }
    }

    private async Task ConnectWithOptionsAsync(bool focusPanel)
    {
        if (SelectedNode is not { } node) return;
        var targets = ConnectionTreeOperations.ConnectionsToOpen(node.Model);
        var dialog = new ConnectWithOptionsViewModel(
            node.Name,
            Root is { } root ? ConnectionTreeOperations.PanelNames(root) : ["General"],
            isRdp: targets.Any(t => t.Protocol == CoreProtocolType.RDP),
            supportsViewOnly: targets.Any(t => t.Protocol is CoreProtocolType.VNC or CoreProtocolType.ARD or CoreProtocolType.RDP))
        {
            Panel = focusPanel ? node.Model.Panel : string.Empty,
        };
        if (!await ChooseConnectOptions.Handle(dialog)) return;
        await ConnectSelectedAsync(dialog.ToOptions());
    }

    /// <summary>Sessions opened from <paramref name="node"/> or (for a folder) any connection below it.</summary>
    public IReadOnlyList<SessionTabViewModel> SessionsOf(ConnectionInfo node)
    {
        if (_sessionsDock is null) return [];
        var targets = ConnectionTreeOperations.ConnectionsToOpen(node).ToHashSet();
        return _sessionsDock.Sessions.Where(s => s.Connection is not null && targets.Contains(s.Connection)).ToList();
    }

    private async Task DisconnectSelectedAsync()
    {
        if (SelectedNode is not { } node || _sessionsDock is null) return;
        var sessions = SessionsOf(node.Model);
        if (sessions.Count == 0)
        {
            Log($"\"{node.Name}\" has no open sessions.");
            return;
        }

        if (_settings?.Current.ConfirmCloseConnection == Core.Config.ConfirmCloseEnum.All
            && !await Ask.Handle(new TreeQuestion("Disconnect",
                sessions.Count == 1
                    ? $"Disconnect \"{node.Name}\"?"
                    : $"Close the {sessions.Count} sessions of \"{node.Name}\"?",
                "Disconnect")))
        {
            return;
        }

        foreach (var session in sessions)
            await _sessionsDock.CloseSessionAsync(session);
        Log($"Disconnected {sessions.Count} session{(sessions.Count == 1 ? "" : "s")} of \"{node.Name}\".");
    }

    // ── Editing ───────────────────────────────────────────────────────────

    /// <summary>Suggestion lists for the connection editor.</summary>
    /// <summary>Names of the configured external tools (for the dialog's before/after/IntApp fields).</summary>
    public Func<IReadOnlyList<string>>? ExternalToolNames { get; set; }

    public ConnectionDialogOptions CreateDialogOptions(bool isDefaultConnection = false) => new()
    {
        IsDefaultConnection = isDefaultConnection,
        Panels = Root is { } root ? ConnectionTreeOperations.PanelNames(root) : ["General"],
        SshTunnels = Root is { } r ? ConnectionTreeOperations.SshTunnelCandidates(r) : [],
        PuttySessions = _puttySessionsTree?.Root.Children.Select(s => s.PuttySession).ToList() ?? [],
        ExternalTools = ExternalToolNames?.Invoke() ?? [],
    };

    private async Task NewConnectionAsync()
    {
        var (container, after) = GetInsertTarget();
        var info = CreateFromDefaults(new ConnectionInfo());

        var dialog = new ConnectionDialogViewModel(info, container, isNew: true, CreateDialogOptions());
        if (!await EditNode.Handle(dialog)) return;

        dialog.Apply();
        if (after is not null)
            container.AddChildBelow(info, after);
        else
            container.AddChild(info);
        container.IsExpanded = true;
        SelectedNode = FindNode(info);
        Log($"Connection '{info.Name}' added ({info.Protocol} {info.Hostname}:{info.Port}).");
    }

    private async Task NewFolderAsync()
    {
        var (container, after) = GetInsertTarget();
        var folder = CreateFromDefaults(new ContainerInfo());

        var dialog = new ConnectionDialogViewModel(folder, container, isNew: true, CreateDialogOptions());
        if (!await EditNode.Handle(dialog)) return;

        dialog.Apply();
        if (after is not null)
            container.AddChildBelow(folder, after);
        else
            container.AddChild(folder);
        container.IsExpanded = true;
        SelectedNode = FindNode(folder);
    }

    private async Task EditSelectedAsync()
    {
        if (SelectedNode is not { IsReadOnly: false } node) return;
        if (node.IsRoot)
        {
            // The root only has a name and the file's master password: that is File ▸ Properties.
            await EditFilePropertiesAsync();
            return;
        }

        var dialog = new ConnectionDialogViewModel(node.Model, node.Model.Parent, isNew: false, CreateDialogOptions());
        if (!await EditNode.Handle(dialog)) return;

        if (dialog.Apply())
        {
            // Inheritance flags raise no notifications; make sure the change counts and is displayed.
            _tracker?.MarkDirty();
            foreach (var vm in node.SelfAndDescendants())
                vm.RefreshDisplay();
        }
    }

    /// <summary>Starts renaming <paramref name="node"/> in place.</summary>
    public void BeginRename(ConnectionNodeViewModel node)
    {
        if (node.IsReadOnly) return;
        foreach (var other in Nodes.SelectMany(n => n.SelfAndDescendants()).Where(n => n.IsEditing && !ReferenceEquals(n, node)))
            CommitRename(other);
        node.EditName = node.Name;
        node.IsEditing = true;
    }

    /// <summary>Ends in-place renaming, keeping the typed name when it is not blank. True when renamed.</summary>
    public bool CommitRename(ConnectionNodeViewModel node)
    {
        if (!node.IsEditing) return false;
        node.IsEditing = false;
        var name = node.EditName.Trim();
        if (name.Length == 0 || name == node.Name) return false;
        node.Model.Name = name;
        return true;
    }

    /// <summary>Ends in-place renaming without changing the name.</summary>
    public void CancelRename(ConnectionNodeViewModel node) => node.IsEditing = false;

    private void DuplicateSelected()
    {
        if (SelectedNode is not { IsRoot: false } node) return;
        if (node.Model is PuttySessionNodeInfo session)
        {
            // Copy the saved session into the connection tree.
            var copy = session.Clone();
            AddConnection(copy, Root);
            Log($"Copied PuTTY session \"{session.Name}\" into the connection tree.");
            return;
        }

        var duplicate = ConnectionTreeOperations.Duplicate(node.Model);
        SelectedNode = FindNode(duplicate);
    }

    private async Task DeleteSelectedAsync()
    {
        if (SelectedNode is not { IsRoot: false, IsReadOnly: false } node || node.Model.Parent is not { } parent) return;

        string message;
        if (node.Model is ContainerInfo folder)
        {
            var count = ConnectionTreeOperations.CountDescendants(folder);
            message = count == 0
                ? $"Delete the empty folder \"{node.Name}\"?"
                : $"Delete the folder \"{node.Name}\" and the {count} item{(count == 1 ? "" : "s")} it contains?";
        }
        else
        {
            message = $"Delete the connection \"{node.Name}\"?";
        }

        if (!await Confirm.Handle(("Delete", message))) return;

        var index = parent.Children.IndexOf(node.Model);
        if (ReferenceEquals(_cutNode, node.Model) || (_cutNode is not null && IsDescendantOf(_cutNode, node.Model)))
        {
            _cutNode = null;
            this.RaisePropertyChanged(nameof(HasCutNode));
        }

        parent.RemoveChild(node.Model);

        // Select a neighbour so keyboard deletes can continue.
        var next = parent.Children.Count == 0
            ? parent
            : parent.Children[Math.Min(index, parent.Children.Count - 1)];
        SelectedNode = FindNode(next);
    }

    private async Task CopyHostnameAsync()
    {
        if (SelectedNode is not { IsFolder: false } node || string.IsNullOrWhiteSpace(node.Hostname)) return;
        await CopyToClipboard.Handle(node.Hostname);
        Log($"Copied host name \"{node.Hostname}\".");
    }

    private void SortSelected(ListSortDirection direction)
    {
        var container = SelectedNode switch
        {
            { IsReadOnly: true } => null,
            { Model: ContainerInfo folder } => folder,
            { Model.Parent: { } parent } => parent,
            _ => Root,
        };
        if (container is not null)
            ConnectionTreeOperations.SortRecursive(container, direction);
    }

    private void SetExpandedAll(bool expanded)
    {
        foreach (var root in Nodes.Select(n => n.Model).OfType<ContainerInfo>())
        {
            ConnectionTreeOperations.SetExpandedRecursive(root, expanded);
            // Keep the roots open so the tree is never reduced to a single line.
            root.IsExpanded = true;
        }
    }

    private void MoveSelected(bool up)
    {
        if (SelectedNode is not { IsRoot: false } node) return;
        var model = node.Model;
        var moved = up ? ConnectionTreeOperations.MoveUp(model) : ConnectionTreeOperations.MoveDown(model);
        if (moved)
            SelectedNode = FindNode(model);
    }

    private void CutSelected()
    {
        if (SelectedNode is not { IsRoot: false, IsReadOnly: false } node) return;
        if (_cutNode is not null && FindNode(_cutNode) is { } previous)
            previous.IsCut = false;
        _cutNode = node.Model;
        node.IsCut = true;
        this.RaisePropertyChanged(nameof(HasCutNode));
    }

    private void PasteIntoSelection()
    {
        if (_cutNode is null) return;
        var (container, after) = GetInsertTarget();
        var index = after is null ? -1 : container.Children.IndexOf(after) + 1;
        if (!ConnectionTreeOperations.CanMoveInto(_cutNode, container))
        {
            Log($"Cannot move \"{_cutNode.Name}\" into \"{container.Name}\".", LogLevel.Warning);
            return;
        }

        var node = _cutNode;
        _cutNode = null;
        this.RaisePropertyChanged(nameof(HasCutNode));
        if (ReferenceEquals(after, node)) return; // pasting onto itself: leave in place

        ConnectionTreeOperations.MoveInto(node, container, index);
        container.IsExpanded = true;
        var vm = FindNode(node);
        if (vm is not null)
        {
            vm.IsCut = false;
            SelectedNode = vm;
        }
    }

    // ── Inheritance and defaults ──────────────────────────────────────────

    private void ApplyInheritanceToChildren()
    {
        if (SelectedNode is not { Model: ContainerInfo folder, IsReadOnly: false } node) return;
        var count = ConnectionTreeOperations.ApplyInheritanceToChildren(folder);
        if (count == 0) return;
        _tracker?.MarkDirty();
        foreach (var vm in node.SelfAndDescendants())
            vm.RefreshDisplay();
        Log($"Applied the inheritance settings of \"{folder.Name}\" to {count} item{(count == 1 ? "" : "s")}.");
    }

    private void ApplyDefaultInheritance()
    {
        if (SelectedNode is not { IsRoot: false, IsReadOnly: false } node) return;
        DefaultConnectionSettings.ApplyInheritance(DefaultConnection, node.Model);
        _tracker?.MarkDirty();
        foreach (var vm in node.SelfAndDescendants())
            vm.RefreshDisplay();
        Log($"Applied the default inheritance to \"{node.Name}\".");
    }

    private async Task EditDefaultConnectionAsync()
    {
        // Edit a copy so Cancel leaves the defaults untouched.
        var copy = DefaultConnectionSettings.Load(null, null);
        DefaultConnectionSettings.ApplyTo(DefaultConnection, copy);
        var dialog = new ConnectionDialogViewModel(copy, null, isNew: false, CreateDialogOptions(isDefaultConnection: true));
        if (!await EditNode.Handle(dialog)) return;

        dialog.Apply();
        _defaultConnection = copy;
        var (values, inheritance) = DefaultConnectionSettings.Save(copy);
        if (_settings is not null)
        {
            _settings.Update(s =>
            {
                s.DefaultConnectionValues = values;
                s.DefaultConnectionInheritance = inheritance;
            });
        }
        Log("Default connection properties saved; they apply to new connections and folders.");
    }

    private ConnectionInfo LoadDefaultConnection()
    {
        var settings = _settings?.Current;
        var template = DefaultConnectionSettings.Load(settings?.DefaultConnectionValues, settings?.DefaultConnectionInheritance);

        // Until the default connection is edited, new connections use the Options ▸ Connections protocol.
        if (settings is not null && string.IsNullOrWhiteSpace(settings.DefaultConnectionValues)
            && MapDefaultProtocol(settings.DefaultProtocol) is { } protocol)
        {
            template.Protocol = protocol;
            template.Port = settings.GetDefaultPort(protocol) ?? ConnectionInfo.GetDefaultPort(protocol);
        }
        return template;
    }

    private static CoreProtocolType? MapDefaultProtocol(string? name) => name?.Trim().ToUpperInvariant() switch
    {
        "SSH" or "SSH2" => CoreProtocolType.SSH2,
        "RDP" => CoreProtocolType.RDP,
        "VNC" => CoreProtocolType.VNC,
        "TELNET" => CoreProtocolType.Telnet,
        "HTTP" => CoreProtocolType.HTTP,
        "HTTPS" => CoreProtocolType.HTTPS,
        _ => null,
    };

    private async Task EditFilePropertiesAsync()
    {
        if (_connectionsService.ConnectionTreeModel is null) return;
        var dialog = new FilePropertiesViewModel(_connectionsService);
        if (!await EditFileProperties.Handle(dialog)) return;

        if (dialog.Apply())
        {
            // Cipher settings live in ConnectionsService and raise no tree notification.
            _tracker?.MarkDirty();
            Log("Connection file security settings changed; they are applied when the file is saved.");
        }
    }

    // ── Wake-on-LAN ───────────────────────────────────────────────────────

    private async Task WakeSelectedAsync()
    {
        if (SelectedNode is not { } node) return;
        var targets = ConnectionTreeOperations.ConnectionsToOpen(node.Model)
            .Where(c => !string.IsNullOrWhiteSpace(c.MacAddress))
            .ToList();
        if (targets.Count == 0)
        {
            Log($"\"{node.Name}\" has no MAC address; set one under Connection ▸ Wake-on-LAN.", LogLevel.Warning);
            return;
        }

        foreach (var target in targets)
        {
            try
            {
                await WakeOnLan.SendAsync(target.MacAddress);
                Log($"Wake-on-LAN packet sent to \"{target.Name}\" ({target.MacAddress}).");
            }
            catch (Exception ex) when (ex is FormatException or System.Net.Sockets.SocketException)
            {
                Log($"Wake-on-LAN for \"{target.Name}\" failed: {ex.Message}", LogLevel.Error);
            }
        }
    }

    // ── PuTTY sessions ────────────────────────────────────────────────────

    /// <summary>Reloads the saved PuTTY sessions shown under the "PuTTY Sessions" root.</summary>
    public async Task RefreshPuttySessionsAsync()
    {
        if (_puttySessionsTree is null) return;
        try
        {
            await _puttySessionsTree.RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log($"Could not read PuTTY sessions: {ex.Message}", LogLevel.Warning);
        }
        SyncPuttyRoot();
    }

    /// <summary>Shows <paramref name="sessions"/> under the "PuTTY Sessions" root (hidden when empty).</summary>
    public void SetPuttySessions(IReadOnlyList<PuttySession> sessions)
    {
        if (_puttySessionsTree is null) return;
        _puttySessionsTree.Apply(sessions);
        SyncPuttyRoot();
    }

    /// <summary>
    /// The PuTTY root's nodes are maintained in place by <see cref="PuttySessionsTree"/> (shared with the
    /// SSH tunnel lookup); this only adds or removes the root itself depending on whether it has sessions.
    /// </summary>
    private void SyncPuttyRoot()
    {
        if (_puttySessionsTree is null) return;
        var hasSessions = _puttySessionsTree.Root.Children.Count > 0;

        if (hasSessions && _puttyRootNode is null)
        {
            _puttyRootNode = new ConnectionNodeViewModel(_puttySessionsTree.Root, null, this);
            Nodes.Add(_puttyRootNode);
        }
        else if (!hasSessions && _puttyRootNode is not null)
        {
            if (SelectedNode is { IsReadOnly: true })
                SelectedNode = Nodes.FirstOrDefault();
            Nodes.Remove(_puttyRootNode);
            _puttyRootNode.Dispose();
            _puttyRootNode = null;
        }

        this.RaisePropertyChanged(nameof(PuttyRootNode));
        if (!string.IsNullOrWhiteSpace(SearchFilter))
            ApplyFilter();
    }

    // ── Drag & drop ───────────────────────────────────────────────────────

    /// <summary>True when <paramref name="source"/> may be dropped on <paramref name="target"/>.</summary>
    public bool CanDrop(ConnectionNodeViewModel source, ConnectionNodeViewModel target, TreeDropPosition position)
    {
        if (ReferenceEquals(source, target) || source.IsRoot || source.IsReadOnly || target.IsReadOnly) return false;
        var container = position == TreeDropPosition.Into ? target.Model as ContainerInfo : target.Model.Parent;
        return container is not null && ConnectionTreeOperations.CanMoveInto(source.Model, container);
    }

    public void Drop(ConnectionNodeViewModel source, ConnectionNodeViewModel target, TreeDropPosition position)
    {
        if (!CanDrop(source, target, position)) return;
        var model = source.Model;
        if (position == TreeDropPosition.Into && target.Model is ContainerInfo folder)
        {
            ConnectionTreeOperations.MoveInto(model, folder);
            folder.IsExpanded = true;
        }
        else
        {
            ConnectionTreeOperations.MoveAbove(model, target.Model);
        }
        SelectedNode = FindNode(model);
    }

    // ── Search ────────────────────────────────────────────────────────────

    /// <summary>Called by nodes after their children changed.</summary>
    internal void OnStructureChanged()
    {
        if (string.IsNullOrWhiteSpace(SearchFilter) || _filterRefreshPending) return;
        // Child view models of a newly added folder sync after this notification; filter once they exist.
        _filterRefreshPending = true;
        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _filterRefreshPending = false;
            ApplyFilter();
        });
    }

    private void ApplyFilter()
    {
        if (Root is null || Nodes.Count == 0) return;
        var allNodes = Nodes.SelectMany(n => n.SelfAndDescendants()).ToList();
        var roots = Nodes.Select(n => n.Model).OfType<ContainerInfo>().ToList();

        if (string.IsNullOrWhiteSpace(SearchFilter))
        {
            foreach (var node in allNodes)
                node.IsVisible = true;
            if (_expansionBeforeSearch is not null)
            {
                foreach (var (container, expanded) in _expansionBeforeSearch)
                    container.IsExpanded = expanded;
                _expansionBeforeSearch = null;
            }
            return;
        }

        // Remember the expansion state once, when a search starts, so clearing restores it.
        _expansionBeforeSearch ??= roots
            .SelectMany(r => r.GetRecursiveChildList().OfType<ContainerInfo>().Append(r))
            .ToDictionary(c => c, c => c.IsExpanded);

        var results = roots.Select(r => ConnectionTreeSearch.Filter(r, SearchFilter)).ToList();
        foreach (var node in allNodes)
            node.IsVisible = results.Any(r => r.Visible.Contains(node.Model));
        foreach (var container in results.SelectMany(r => r.ContainersToExpand))
            container.IsExpanded = true;

        // Keep the selection on a visible node.
        if (SelectedNode is { IsVisible: false })
            SelectedNode = allNodes.FirstOrDefault(n => results.Any(r => r.Matches.Contains(n.Model)));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool IsDescendantOf(ConnectionInfo node, ConnectionInfo ancestor)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private static void Log(string message, LogLevel level = LogLevel.Info)
    {
        try
        {
            AppServices.GetRequired<LogPanelDockable>().Log(message, level);
        }
        catch (InvalidOperationException)
        {
            System.Diagnostics.Trace.WriteLine(message);
        }
    }
}
