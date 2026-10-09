using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views;

/// <summary>
/// Connection tree. Besides the bindings this handles keyboard shortcuts and drag &amp; drop:
/// dropping on the middle of a folder moves the node into it, anywhere else moves it above the target.
/// </summary>
public partial class ConnectionTreeView : UserControl
{
    private const string DragFormat = "application/x-mremoteng-tree-node";
    private const double DragThreshold = 5;

    private Point? _pressPoint;
    private ConnectionNodeViewModel? _pressNode;
    private TreeViewItem? _dropHighlight;

    public ConnectionTreeView()
    {
        InitializeComponent();

        Tree.AddHandler(KeyDownEvent, OnTreeKeyDown, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerReleasedEvent, (_, _) => _pressPoint = null, RoutingStrategies.Tunnel);
        Tree.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Tree.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDropHighlight());
        Tree.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private ConnectionTreeViewModel? ViewModel => DataContext as ConnectionTreeViewModel;

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Folders toggle expansion on double-click; only connections open a session.
        if (ViewModel is { SelectedNode: { IsFolder: false } } vm)
            Execute(vm.ConnectSelectedCommand);
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        ICommand? command = (e.Key, ctrl) switch
        {
            (Key.F2, false) or (Key.Enter, false) => vm.EditSelectedCommand,
            (Key.Delete, false) => vm.DeleteSelectedCommand,
            (Key.D, true) => vm.DuplicateSelectedCommand,
            (Key.X, true) => vm.CutCommand,
            (Key.V, true) => vm.PasteCommand,
            (Key.Up, true) => vm.MoveUpCommand,
            (Key.Down, true) => vm.MoveDownCommand,
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

    // ── Drag & drop ───────────────────────────────────────────────────────

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(Tree);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _pressPoint = null;
            return;
        }

        _pressNode = FindItem(e.Source)?.DataContext as ConnectionNodeViewModel;
        _pressPoint = _pressNode is { IsRoot: false } ? point.Position : null;
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
