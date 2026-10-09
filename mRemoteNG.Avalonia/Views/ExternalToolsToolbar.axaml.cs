using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views;

/// <summary>
/// External Tools toolbar: one button per tool marked "Show on toolbar", run for the selected tree connection.
/// Without an explicit DataContext it uses the application's <see cref="ExternalToolsToolbarViewModel"/>.
/// Right-click: "Show Text" and "External Tools…" (opens <see cref="ExternalToolsWindow"/>).
/// </summary>
public partial class ExternalToolsToolbar : UserControl
{
    private ExternalToolsToolbarViewModel? _subscribed;

    public ExternalToolsToolbar()
    {
        InitializeComponent();
    }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        if (DataContext is not ExternalToolsToolbarViewModel && !Design.IsDesignMode)
            DataContext = AppServices.GetRequired<ExternalToolsToolbarViewModel>();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribed is not null)
            _subscribed.ManageRequested -= OnManageRequested;
        _subscribed = DataContext as ExternalToolsToolbarViewModel;
        if (_subscribed is not null)
            _subscribed.ManageRequested += OnManageRequested;
    }

    private async void OnManageRequested(object? sender, EventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner && sender is ExternalToolsToolbarViewModel vm)
            await ExternalToolsWindow.ShowAsync(owner, vm.SelectedConnection);
    }
}
