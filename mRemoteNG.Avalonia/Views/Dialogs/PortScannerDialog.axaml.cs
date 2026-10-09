using Avalonia.Controls;
using mRemoteNG.Avalonia.Services;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Localization;

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
        CloseButton.Click += (_, _) => Close();
    }

    private static string ImportIntoTree(IReadOnlyList<mRemoteNG.Core.Tools.PortScanning.ScanHost> hosts,
        mRemoteNG.Core.Connection.Protocol.ProtocolType protocol)
    {
        var (result, folder) = ConnectionTreeImporter.ForApp().Import(new PortScanImporter(hosts, protocol), "the port scan");
        var text = Localizer.Format("PortScanImportedFormat", result.ConnectionCount, protocol, folder);
        var skipped = result.Warnings.Count;
        return skipped > 0 ? text + " " + Localizer.Format("PortScanSkippedFormat", skipped, protocol) : text;
    }
}
