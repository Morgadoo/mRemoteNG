using Avalonia.Controls;
using Avalonia.Threading;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Localization;
using mRemoteNG.Protocols.External;

namespace mRemoteNG.Avalonia.Views;

/// <summary>
/// Fills an "External Tools" submenu (legacy tree context menu "External Tools ▸ tool") with one item per tool,
/// rebuilt whenever the tool list changes. Tools for another operating
/// system are shown disabled.
/// </summary>
public static class ExternalToolsMenu
{
    /// <summary>
    /// Keeps <paramref name="parent"/>'s items in sync with the tools; each item runs its tool for the connection
    /// <paramref name="target"/> returns when clicked (a folder runs it for every connection inside).
    /// </summary>
    public static void Attach(MenuItem parent, ExternalToolsService service, Func<ConnectionInfo?> target)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(target);

        Populate(parent, service, target);
        void Refresh()
        {
            if (Dispatcher.UIThread.CheckAccess())
                Populate(parent, service, target);
            else
                Dispatcher.UIThread.Post(() => Populate(parent, service, target));
        }
        service.ToolsChanged += (_, _) => Refresh();
        service.Tools.CollectionChanged += (_, _) => Refresh();
    }

    /// <summary>Replaces <paramref name="parent"/>'s items with the current tools.</summary>
    public static void Populate(MenuItem parent, ExternalToolsService service, Func<ConnectionInfo?> target)
    {
        parent.Items.Clear();
        foreach (var item in BuildItems(service, target))
            parent.Items.Add(item);
        if (parent.Items.Count == 0)
            parent.Items.Add(new MenuItem { Header = Localizer.Menu("NoExternalTools"), IsEnabled = false });
    }

    public static IReadOnlyList<MenuItem> BuildItems(ExternalToolsService service, Func<ConnectionInfo?> target)
    {
        var items = new List<MenuItem>();
        foreach (var tool in service.Tools)
        {
            var command = ExternalToolsToolbarViewModel.CreateItem(service, tool, target);
            var item = new MenuItem
            {
                Header = tool.IsAvailableOnCurrentPlatform ? tool.DisplayName : $"{tool.DisplayName} ({tool.Platform})",
                Command = command.RunCommand,
                IsEnabled = tool.IsAvailableOnCurrentPlatform,
            };
            if (command.Icon is { } icon)
                item.Icon = new Image { Source = icon, Width = 16, Height = 16 };
            items.Add(item);
        }
        return items;
    }
}
