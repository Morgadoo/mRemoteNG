using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Config.Connections;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;
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
            if (IsFolder)
                return string.IsNullOrEmpty(Description) ? Name : $"{Name}\n{Description}";
            var host = string.IsNullOrEmpty(Hostname) ? "(no hostname)" : Hostname;
            var target = Port > 0 ? $"{host}:{Port}" : host;
            var text = $"{Model.Protocol}  {target}";
            if (!string.IsNullOrEmpty(Username)) text += $"\nUser: {Username}";
            if (!string.IsNullOrEmpty(Description)) text += $"\n{Description}";
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

/// <summary>
/// ViewModel for the connection tree panel. Wraps the Core <see cref="ConnectionTreeModel"/> held by
/// <see cref="ConnectionsService"/>; every edit is made on the Core tree and the view models follow.
/// </summary>
public sealed class ConnectionTreeViewModel : ReactiveObject
{
    private readonly ConnectionsService _connectionsService;
    private ConnectionTreeChangeTracker? _tracker;
    private string _searchFilter = string.Empty;
    private Dictionary<ContainerInfo, bool>? _expansionBeforeSearch;
    private bool _filterRefreshPending;
    private ConnectionNodeViewModel? _selectedNode;
    private ConnectionInfo? _cutNode;
    private bool _isDirty;
    private SessionsDockable? _sessionsDock;
    private IProtocolFactory? _protocolFactory;

    public ConnectionTreeViewModel(ConnectionsService connectionsService)
    {
        _connectionsService = connectionsService;

        var selection = this.WhenAnyValue(x => x.SelectedNode).ObserveOn(RxApp.MainThreadScheduler);
        var hasConnection = selection.Select(n => n is { IsFolder: false });
        var hasNode = selection.Select(n => n is not null);
        var hasNonRoot = selection.Select(n => n is { IsRoot: false });

        ConnectSelectedCommand = ReactiveCommand.CreateFromTask(ConnectSelectedAsync, hasConnection);
        NewFolderCommand = ReactiveCommand.CreateFromTask(NewFolderAsync);
        NewConnectionCommand = ReactiveCommand.CreateFromTask(NewConnectionAsync);
        EditSelectedCommand = ReactiveCommand.CreateFromTask(EditSelectedAsync, hasNode);
        DuplicateSelectedCommand = ReactiveCommand.Create(DuplicateSelected, hasNonRoot);
        DeleteSelectedCommand = ReactiveCommand.CreateFromTask(DeleteSelectedAsync, hasNonRoot);
        SortCommand = ReactiveCommand.Create(Sort);
        MoveUpCommand = ReactiveCommand.Create(() => MoveSelected(up: true), hasNonRoot);
        MoveDownCommand = ReactiveCommand.Create(() => MoveSelected(up: false), hasNonRoot);
        CutCommand = ReactiveCommand.Create(CutSelected, hasNonRoot);
        PasteCommand = ReactiveCommand.Create(PasteIntoSelection,
            this.WhenAnyValue(x => x.HasCutNode).ObserveOn(RxApp.MainThreadScheduler));
        ClearSearchCommand = ReactiveCommand.Create(() => { SearchFilter = string.Empty; });

        foreach (var command in new IHandleObservableErrors[]
                 {
                     ConnectSelectedCommand, NewFolderCommand, NewConnectionCommand, EditSelectedCommand,
                     DuplicateSelectedCommand, DeleteSelectedCommand, SortCommand, MoveUpCommand,
                     MoveDownCommand, CutCommand, PasteCommand,
                 })
        {
            command.ThrownExceptions.Subscribe(ex => Log($"Connection tree: {ex.Message}", LogLevel.Error));
        }

        CreateNewTree();
    }

    public ObservableCollection<ConnectionNodeViewModel> Nodes { get; } = [];

    /// <summary>The Core root node currently shown.</summary>
    public RootNodeInfo? Root => _connectionsService.ConnectionTreeModel?.RootNode;

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

    public bool HasCutNode => _cutNode is not null;

    // ── Interactions (handled by the view) ────────────────────────────────

    /// <summary>Shows the connection editor; returns true when the user pressed OK.</summary>
    public Interaction<ConnectionDialogViewModel, bool> EditNode { get; } = new();

    /// <summary>Asks a yes/no question (title, message); returns true for yes.</summary>
    public Interaction<(string Title, string Message), bool> Confirm { get; } = new();

    // ── Commands ──────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> ConnectSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> NewFolderCommand { get; }
    public ReactiveCommand<Unit, Unit> NewConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> EditSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> DuplicateSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteSelectedCommand { get; }
    public ReactiveCommand<Unit, Unit> SortCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
    public ReactiveCommand<Unit, Unit> CutCommand { get; }
    public ReactiveCommand<Unit, Unit> PasteCommand { get; }
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
        foreach (var node in Nodes)
            node.Dispose();
        Nodes.Clear();
        _cutNode = null;
        this.RaisePropertyChanged(nameof(HasCutNode));
        _expansionBeforeSearch = null;

        model.RootNode.IsExpanded = true;
        var rootVm = new ConnectionNodeViewModel(model.RootNode, null, this);
        Nodes.Add(rootVm);
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

        if (Nodes.Count != 1 || !ReferenceEquals(Nodes[0].Model, model.RootNode))
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

    /// <summary>
    /// The folder new nodes go into: the selected folder, else the selected connection's folder
    /// (returned as <c>after</c> so the new node lands below it), else the root.
    /// </summary>
    private (ContainerInfo container, ConnectionInfo? after) GetInsertTarget()
    {
        var root = Root ?? throw new InvalidOperationException("No connection tree loaded.");
        return SelectedNode?.Model switch
        {
            ContainerInfo folder => (folder, null),
            { Parent: { } parent } connection => (parent, connection),
            _ => (root, null),
        };
    }

    // ── Commands implementation ───────────────────────────────────────────

    private async Task ConnectSelectedAsync()
    {
        if (SelectedNode is { IsFolder: false } node)
            await ConnectAsync(node);
    }

    /// <summary>Opens a session for <paramref name="node"/> using its resolved (inherited) settings.</summary>
    public async Task ConnectAsync(ConnectionNodeViewModel node)
    {
        if (node.IsFolder || _sessionsDock is null || _protocolFactory is null) return;

        ConnectionParameters parameters;
        try
        {
            parameters = ConnectionParametersFactory.FromConnectionInfo(node.Model);
        }
        catch (NotSupportedException ex)
        {
            _sessionsDock.ReportError($"Cannot connect to \"{node.Name}\": {ex.Message}");
            return;
        }

        try
        {
            await _sessionsDock.OpenConnectionAsync(parameters, _protocolFactory);
        }
        catch (Exception ex)
        {
            _sessionsDock.ReportError($"Could not connect to \"{node.Name}\": {ex.Message}");
        }
    }

    private async Task NewConnectionAsync()
    {
        var (container, after) = GetInsertTarget();
        var info = ConnectionDefaults.ApplyNewConnectionDefaults(new ConnectionInfo());
        info.Protocol = CoreProtocolType.SSH2;
        info.Port = ConnectionInfo.GetDefaultPort(info.Protocol);

        var dialog = new ConnectionDialogViewModel(info, container, isNew: true);
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
        var folder = ConnectionDefaults.ApplyNewConnectionDefaults(new ContainerInfo());

        var dialog = new ConnectionDialogViewModel(folder, container, isNew: true);
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
        if (SelectedNode is not { } node) return;
        var dialog = new ConnectionDialogViewModel(node.Model, node.Model.Parent, isNew: false);
        if (!await EditNode.Handle(dialog)) return;

        if (dialog.Apply())
        {
            // Inheritance flags raise no notifications; make sure the change counts and is displayed.
            _tracker?.MarkDirty();
            foreach (var vm in node.SelfAndDescendants())
                vm.RefreshDisplay();
        }
    }

    private void DuplicateSelected()
    {
        if (SelectedNode is not { IsRoot: false } node) return;
        var copy = ConnectionTreeOperations.Duplicate(node.Model);
        SelectedNode = FindNode(copy);
    }

    private async Task DeleteSelectedAsync()
    {
        if (SelectedNode is not { IsRoot: false } node || node.Model.Parent is not { } parent) return;

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

    private void Sort()
    {
        var container = SelectedNode?.Model as ContainerInfo ?? SelectedNode?.Model.Parent ?? Root;
        container?.Sort();
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
        if (SelectedNode is not { IsRoot: false } node) return;
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

    // ── Drag & drop ───────────────────────────────────────────────────────

    /// <summary>True when <paramref name="source"/> may be dropped on <paramref name="target"/>.</summary>
    public bool CanDrop(ConnectionNodeViewModel source, ConnectionNodeViewModel target, TreeDropPosition position)
    {
        if (ReferenceEquals(source, target) || source.IsRoot) return false;
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
        _expansionBeforeSearch ??= Root.GetRecursiveChildList()
            .OfType<ContainerInfo>()
            .Append(Root)
            .ToDictionary(c => c, c => c.IsExpanded);

        var result = ConnectionTreeSearch.Filter(Root, SearchFilter);
        foreach (var node in allNodes)
            node.IsVisible = result.Visible.Contains(node.Model);
        foreach (var container in result.ContainersToExpand)
            container.IsExpanded = true;

        // Keep the selection on a visible node.
        if (SelectedNode is { IsVisible: false })
            SelectedNode = allNodes.FirstOrDefault(n => result.Matches.Contains(n.Model));
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
