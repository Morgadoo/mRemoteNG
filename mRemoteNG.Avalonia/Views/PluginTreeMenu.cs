using Avalonia.Controls;
using mRemoteNG.Core.Connection;
using mRemoteNG.ExternalProviders.Plugins;
using mRemoteNG.PluginContracts;
using ReactiveUI;

namespace mRemoteNG.Avalonia.Views;

/// <summary>
/// Adds the plugins' tree context actions (<see cref="ITreeContextActionPlugin"/>) that apply to the selected node to
/// the connection tree's context menu, below <c>anchor</c>, each time the menu opens.
/// </summary>
public static class PluginTreeMenu
{
    private const string ItemTag = "plugin-tree-action";

    public static void Attach(ContextMenu menu, Control anchor, PluginHost host, Func<ConnectionInfo?> target, Action executed)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(executed);

        menu.Opening += (_, _) =>
        {
            foreach (var item in menu.Items.OfType<Control>().Where(c => Equals(c.Tag, ItemTag)).ToList())
                menu.Items.Remove(item);

            var node = target();
            var items = node is null ? [] : BuildItems(host, node, executed);
            if (items.Count == 0)
                return;
            var index = menu.Items.IndexOf(anchor) + 1;
            menu.Items.Insert(index++, new Separator { Tag = ItemTag });
            foreach (var item in items)
                menu.Items.Insert(index++, item);
        };
    }

    /// <summary>One menu item per action that applies to <paramref name="node"/>, by the plugins' sort order.</summary>
    public static IReadOnlyList<MenuItem> BuildItems(PluginHost host, ConnectionInfo node, Action? executed = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(node);
        return host.GetTreeActions(node).Select(plugin => new MenuItem
        {
            Tag = ItemTag,
            Header = string.IsNullOrWhiteSpace(plugin.TreeContextMenuAction?.MenuText) ? plugin.DisplayName : plugin.TreeContextMenuAction.MenuText,
            Command = ReactiveCommand.Create(() =>
            {
                if (host.ExecuteTreeAction(plugin, node))
                    executed?.Invoke();
            }),
        }).ToList();
    }
}
