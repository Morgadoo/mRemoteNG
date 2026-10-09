using System.Reactive;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Material.Icons;
using Material.Icons.Avalonia;
using mRemoteNG.Core.Localization;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.Views.Dialogs;
using ReactiveUI;

namespace mRemoteNG.Avalonia.Views;

/// <summary>
/// Connection tree. Besides the bindings this handles keyboard shortcuts, in-place renaming, the tree's
/// dialogs (connect with options, file properties, questions, clipboard) and drag &amp; drop: dropping on
/// the middle of a folder moves the node into it, anywhere else moves it above the target.
/// </summary>
/// <remarks>
/// Keys: Enter connects (folders: every connection inside), F2 renames in place, Ctrl+E / Alt+Enter opens
/// the properties dialog, Delete deletes, Ctrl+D duplicates, Ctrl+Shift+C copies the host name,
/// Ctrl+X / Ctrl+V cut and paste, Ctrl+Up / Ctrl+Down move. The sidebar header and search box are the main
/// window's; <see cref="HandleSearchKey"/> and <see cref="CreateMoreActionsMenu"/> are their hooks into the tree.
/// </remarks>
public partial class ConnectionTreeView : UserControl
{
    private const string DragFormat = "application/x-mremoteng-tree-node";
    private const double DragThreshold = 5;

    private Point? _pressPoint;
    private ConnectionNodeViewModel? _pressNode;
    private TreeViewItem? _dropHighlight;
    private ConnectionTreeViewModel? _boundViewModel;
    private readonly List<IDisposable> _interactionHandlers = [];

    public ConnectionTreeView()
    {
        InitializeComponent();

        // External Tools ▸ <tool> for the selected connection (the designer has no service container).
        if (!Design.IsDesignMode)
        {
            ExternalToolsMenu.Attach(ExternalToolsMenuItem,
                AppServices.GetRequired<Protocols.External.ExternalToolsService>(),
                () => (DataContext as ConnectionTreeViewModel)?.SelectedNode?.Model);
        }

        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerReleasedEvent, (_, _) => _pressPoint = null, RoutingStrategies.Tunnel);
        Tree.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Tree.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropHighlight());
        Tree.AddHandler(DragDrop.DropEvent, OnDrop);

        DataContextChanged += (_, _) => BindInteractions();
    }

    /// <summary>
    /// The command of the empty tree's "Import…" button. When not set, the window's view model
    /// <c>ImportCommand</c> is used (the main window's File ▸ Import).
    /// </summary>
    public static readonly StyledProperty<ICommand?> ImportCommandProperty =
        AvaloniaProperty.Register<ConnectionTreeView, ICommand?>(nameof(ImportCommand));

    public ICommand? ImportCommand
    {
        get => GetValue(ImportCommandProperty);
        set => SetValue(ImportCommandProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ImportCommandProperty)
            UpdateImportCommand();
    }

    private TopLevel? _topLevel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null)
            _topLevel.DataContextChanged += OnTopLevelDataContextChanged;
        UpdateImportCommand();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null)
            _topLevel.DataContextChanged -= OnTopLevelDataContextChanged;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopLevelDataContextChanged(object? sender, EventArgs e) => UpdateImportCommand();

    private void UpdateImportCommand()
    {
        var command = ImportCommand ?? (_topLevel?.DataContext as MainWindowViewModel)?.ImportCommand;
        EmptyImportButton.Command = command;
        EmptyImportButton.IsVisible = command is not null;
    }

    // ── Hooks for the sidebar header and search box (in MainWindow) ─────

    /// <summary>
    /// Keys of the tree's search box (call from its tunnelling KeyDown): Down moves into the results, Enter connects
    /// the selected match. Returns true when the key was used.
    /// </summary>
    public bool HandleSearchKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when e.KeyModifiers == KeyModifiers.None:
                FocusTree();
                return true;
            case Key.Enter when e.KeyModifiers == KeyModifiers.None
                                && ViewModel is { SelectedNode: { IsFolder: false, IsVisible: true } } vm
                                && !string.IsNullOrWhiteSpace(vm.SearchFilter):
                Execute(vm.ConnectSelectedCommand);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Moves the keyboard to the selected row (or the tree).</summary>
    public void FocusTree()
    {
        if (Tree.SelectedItem is { } selected && Tree.TreeContainerFromItem(selected) is TreeViewItem item)
            item.Focus(NavigationMethod.Directional);
        else
            Tree.Focus(NavigationMethod.Directional);
    }

    /// <summary>
    /// The sidebar header's "⋯" menu: Expand all, Collapse all, Sort ▸ A–Z / Z–A, Refresh PuTTY sessions.
    /// <code>button.Flyout = ConnectionTreeView.CreateMoreActionsMenu(vm.ConnectionTree);</code>
    /// </summary>
    public static MenuFlyout CreateMoreActionsMenu(ConnectionTreeViewModel tree)
    {
        ArgumentNullException.ThrowIfNull(tree);

        static MenuItem Item(string key, ICommand? command, MaterialIconKind? icon = null) => new()
        {
            Header = Localizer.Get(key),
            Command = command,
            Icon = icon is { } kind ? new MaterialIcon { Kind = kind } : null,
        };

        var sort = Item("Sort", null, MaterialIconKind.SortAlphabeticalAscending);
        sort.Items.Add(Item("SortAsc", tree.SortAscendingCommand));
        sort.Items.Add(Item("SortDesc", tree.SortDescendingCommand));
        return new MenuFlyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            ItemsSource = new List<Control>
            {
                Item("ExpandAll", tree.ExpandAllCommand, MaterialIconKind.ArrowExpandVertical),
                Item("CollapseAll", tree.CollapseAllCommand, MaterialIconKind.ArrowCollapseVertical),
                sort,
                new Separator(),
                Item("RefreshPuTTYSessions", tree.RefreshPuttySessionsCommand, MaterialIconKind.Refresh),
            },
        };
    }

    private async void OnRowConnectClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ConnectionNodeViewModel { IsFolder: false } node } || ViewModel is not { } vm)
            return;
        e.Handled = true;
        vm.SelectedNode = node;
        await vm.ConnectAsync(node);
    }

    private ConnectionTreeViewModel? ViewModel => DataContext as ConnectionTreeViewModel;

    // ── Dialogs ───────────────────────────────────────────────────────────

    private void BindInteractions()
    {
        if (ReferenceEquals(_boundViewModel, ViewModel)) return;
        foreach (var handler in _interactionHandlers)
            handler.Dispose();
        _interactionHandlers.Clear();
        _boundViewModel = ViewModel;
        if (_boundViewModel is not { } vm) return;

        _interactionHandlers.Add(vm.ChooseConnectOptions.RegisterHandler(async context =>
        {
            if (Owner is not { } owner)
            {
                context.SetOutput(false);
                return;
            }
            var dialog = new ConnectWithOptionsDialog(context.Input);
            if (!string.IsNullOrEmpty(context.Input.Panel))
                dialog.FocusPanel();
            context.SetOutput(await dialog.ShowDialog<bool>(owner));
        }));
        _interactionHandlers.Add(vm.EditFileProperties.RegisterHandler(async context =>
        {
            context.SetOutput(Owner is { } owner && await new FilePropertiesDialog(context.Input).ShowDialog<bool>(owner));
        }));
        _interactionHandlers.Add(vm.Ask.RegisterHandler(async context =>
        {
            var question = context.Input;
            context.SetOutput(Owner is { } owner
                && await MessageDialog.ConfirmAsync(owner, question.Title, question.Message, question.YesLabel, question.NoLabel));
        }));
        _interactionHandlers.Add(vm.CopyToClipboard.RegisterHandler(async context =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(context.Input);
            context.SetOutput(Unit.Default);
        }));
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    // ── Keyboard / mouse ──────────────────────────────────────────────────

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Folders toggle expansion on double-click; only connections open a session.
        if (e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null) return;
        if (ViewModel is { SelectedNode: { IsFolder: false } } vm)
            Execute(vm.ConnectSelectedCommand);
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        // Keys typed into the in-place rename box belong to it.
        if (e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        ICommand? command = (e.Key, ctrl, shift, alt) switch
        {
            (Key.Enter, false, false, false) => vm.ConnectSelectedCommand,
            (Key.Enter, false, false, true) or (Key.E, true, false, false) => vm.EditSelectedCommand,
            (Key.F2, false, false, false) => vm.RenameSelectedCommand,
            (Key.Delete, false, false, false) => vm.DeleteSelectedCommand,
            (Key.D, true, false, false) => vm.DuplicateSelectedCommand,
            (Key.C, true, true, false) => vm.CopyHostnameCommand,
            (Key.X, true, false, false) => vm.CutCommand,
            (Key.V, true, false, false) => vm.PasteCommand,
            (Key.Up, true, false, false) => vm.MoveUpCommand,
            (Key.Down, true, false, false) => vm.MoveDownCommand,
            _ => null,
        };

        if (command is null) return;
        Execute(command);
        e.Handled = true;
    }

    private static void Execute(ICommand command)
    {
        if (command.CanExecute(null))
            command.Execute(null);
    }

    // ── In-place rename ───────────────────────────────────────────────────

    private void OnRenameBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty || sender is not TextBox { IsVisible: true } box) return;
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ConnectionNodeViewModel node } || ViewModel is not { } vm) return;
        switch (e.Key)
        {
            case Key.Enter:
                vm.CommitRename(node);
                e.Handled = true;
                Tree.Focus();
                break;
            case Key.Escape:
                vm.CancelRename(node);
                e.Handled = true;
                Tree.Focus();
                break;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ConnectionNodeViewModel { IsEditing: true } node } && ViewModel is { } vm)
            vm.CommitRename(node);
    }

    // ── Drag & drop ───────────────────────────────────────────────────────

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(Tree);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _pressPoint = null;
            return;
        }

        if (e.Source is Visual source && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
        {
            _pressPoint = null;
            return;
        }

        _pressNode = FindItem(e.Source)?.DataContext as ConnectionNodeViewModel;
        _pressPoint = _pressNode is { IsRoot: false, IsReadOnly: false } ? point.Position : null;
    }

    private async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressPoint is not { } start || _pressNode is null) return;
        if (!e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed)
        {
            _pressPoint = null;
            return;
        }

        var delta = e.GetPosition(Tree) - start;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;

        _pressPoint = null;
        var data = new DataObject();
        data.Set(DragFormat, _pressNode);
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        finally
        {
            ClearDropHighlight();
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        if (!TryGetDrop(e, out var source, out var target, out var position, out var item))
        {
            ClearDropHighlight();
            return;
        }

        if (ViewModel!.CanDrop(source, target, position))
        {
            e.DragEffects = DragDropEffects.Move;
            SetDropHighlight(item, position);
        }
        else
        {
            ClearDropHighlight();
        }
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        ClearDropHighlight();
        if (!TryGetDrop(e, out var source, out var target, out var position, out _)) return;
        ViewModel!.Drop(source, target, position);
        e.Handled = true;
    }

    private bool TryGetDrop(DragEventArgs e, out ConnectionNodeViewModel source, out ConnectionNodeViewModel target,
        out TreeDropPosition position, out TreeViewItem item)
    {
        source = null!;
        target = null!;
        item = null!;
        position = TreeDropPosition.Into;

        if (ViewModel is null || e.Data.Get(DragFormat) is not ConnectionNodeViewModel dragged) return false;
        if (FindItem(e.Source) is not { DataContext: ConnectionNodeViewModel over } overItem) return false;

        source = dragged;
        target = over;
        item = overItem;

        if (over.IsRoot)
        {
            position = TreeDropPosition.Into;
            return true;
        }

        // Header height: the item's own row, excluding its expanded children.
        var y = e.GetPosition(overItem).Y;
        var header = overItem.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.Name == "PART_Header")?.Bounds.Height ?? 24;
        position = over.IsFolder && y > header * 0.25 ? TreeDropPosition.Into : TreeDropPosition.Before;
        return true;
    }

    private static TreeViewItem? FindItem(object? source) =>
        (source as Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true);

    private void SetDropHighlight(TreeViewItem item, TreeDropPosition position)
    {
        if (!ReferenceEquals(_dropHighlight, item))
            ClearDropHighlight();
        _dropHighlight = item;
        item.Classes.Set("drop-into", position == TreeDropPosition.Into);
        item.Classes.Set("drop-before", position == TreeDropPosition.Before);
    }

    private void ClearDropHighlight()
    {
        if (_dropHighlight is null) return;
        _dropHighlight.Classes.Remove("drop-into");
        _dropHighlight.Classes.Remove("drop-before");
        _dropHighlight = null;
    }
}
