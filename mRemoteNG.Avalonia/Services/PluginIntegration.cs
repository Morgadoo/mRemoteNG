using Avalonia.Threading;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Localization;
using mRemoteNG.ExternalProviders.Plugins;
using LogLevel = mRemoteNG.Avalonia.ViewModels.Docking.LogLevel;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Connects the <see cref="PluginHost"/> to the application and loads the plugins: plugin messages go to the log
/// panel (warnings and errors not marked "log only" also as a toast), connections a plugin imports are added at
/// the tree's insert target, and the plugins' connection properties appear in the connection editor.
/// </summary>
public static class PluginIntegration
{
    public static void Start(PluginHost host, ConnectionTreeViewModel tree, LogPanelDockable log, ToastService toasts)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(toasts);

        host.MessageWritten += (_, message) =>
        {
            log.Log(message.Text, message.Level switch
            {
                PluginMessageLevel.Error => LogLevel.Error,
                PluginMessageLevel.Warning => LogLevel.Warning,
                _ => LogLevel.Info,
            });
            if (!message.LogOnly && message.Level != PluginMessageLevel.Information)
            {
                toasts.Show(Localizer.Get("Plugins"), message.Text,
                    message.Level == PluginMessageLevel.Error ? ToastLevel.Error : ToastLevel.Warning);
            }
        };
        host.ImportHandler = nodes =>
        {
            if (Dispatcher.UIThread.CheckAccess())
                Import(tree, log, nodes);
            else
                Dispatcher.UIThread.Invoke(() => Import(tree, log, nodes));
        };
        tree.PluginProperties = host.GetConnectionProperties;

        host.Load();
        foreach (var entry in host.Catalog)
        {
            if (entry.State == PluginState.Loaded)
                log.Log(Localizer.Format("PluginLoadedFormat", entry.DisplayName, entry.Version));
            else if (entry.State != PluginState.Disabled)
                log.Log(Localizer.Format("PluginNotLoadedFormat", entry.DisplayName, entry.Message), LogLevel.Warning);
        }
    }

    public static void Import(ConnectionTreeViewModel tree, LogPanelDockable log, IReadOnlyList<ConnectionInfo> nodes)
    {
        if (nodes.Count == 0)
            return;
        if (tree.Root is null)
        {
            log.Log(Localizer.Format("PluginImportNoFileFormat", nodes.Count), LogLevel.Warning);
            return;
        }

        var first = tree.AddConnection(nodes[0]);
        var parent = nodes[0].Parent;
        foreach (var node in nodes.Skip(1))
            tree.AddConnection(node, parent);
        if (first is not null)
            tree.SelectedNode = first;
        log.Log(Localizer.Format("PluginImportedFormat", nodes.Count, parent?.Name ?? string.Empty));
    }
}
