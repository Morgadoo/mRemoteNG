using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;
using mRemoteNG.Core.Localization;

namespace mRemoteNG.Avalonia.Views;

/// <summary>
/// External Tools toolbar: one compact button per tool marked "Show on toolbar", run for the selected tree
/// connection; beyond <see cref="ExternalToolsToolbarViewModel.MaxInlineButtons"/> tools the rest are listed in a
/// "⋯" menu. Without an explicit DataContext it uses the application's <see cref="ExternalToolsToolbarViewModel"/>.
/// Right-click: "Show Text" and "External Tools…" (opens <see cref="ExternalToolsWindow"/>).
/// </summary>
public partial class ExternalToolsToolbar : UserControl
{
    private ExternalToolsToolbarViewModel? _subscribed;

    public ExternalToolsToolbar()
    {
        InitializeComponent();
        OverflowButton.Click += (_, _) => ShowOverflowMenu();
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

    private void ShowOverflowMenu()
    {
        if (_subscribed is not { } vm)
            return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var tool in vm.OverflowButtons)
        {
            var item = new MenuItem
            {
                Header = tool.DisplayName,
                Command = tool.RunCommand,
                Icon = tool.Icon is null ? null : new Image { Source = tool.Icon, Width = 16, Height = 16 },
            };
            ToolTip.SetTip(item, tool.ToolTip);
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new Separator());
        flyout.Items.Add(new MenuItem { Header = Localizer.Get("ExternalTools") + "…", Command = vm.ManageCommand });
        flyout.ShowAt(OverflowButton);
    }

    private async void OnManageRequested(object? sender, EventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner && sender is ExternalToolsToolbarViewModel vm)
            await ExternalToolsWindow.ShowAsync(owner, vm.SelectedConnection);
    }
}
