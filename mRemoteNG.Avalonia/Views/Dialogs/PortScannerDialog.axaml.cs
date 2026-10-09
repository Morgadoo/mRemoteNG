using Avalonia.Controls;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Core.Config.Import;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class PortScannerDialog : Window
{
    /// <summary>Scanner that imports into the app's connection tree.</summary>
    public PortScannerDialog() : this(null) { }

    public PortScannerDialog(PortScannerViewModel? viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? new PortScannerViewModel(ImportIntoTree);
        Closed += (_, _) => (DataContext as PortScannerViewModel)?.Stop();
    }

    private static string ImportIntoTree(IReadOnlyList<mRemoteNG.Core.Tools.PortScanning.ScanHost> hosts,
        mRemoteNG.Core.Connection.Protocol.ProtocolType protocol)
    {
        var (result, folder) = ConnectionTreeImporter.ForApp().Import(new PortScanImporter(hosts, protocol), "the port scan");
        var text = $"Imported {result.ConnectionCount} {protocol} connection(s) into \"{folder}\".";
        var skipped = result.Warnings.Count;
        return skipped > 0 ? $"{text} {skipped} host(s) skipped: no {protocol} service found." : text;
    }
}
