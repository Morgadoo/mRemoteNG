using Avalonia.Controls;
using Avalonia.Input;
using mRemoteNG.Avalonia.ViewModels.Docking;

namespace mRemoteNG.Avalonia.Views.Docking;

public partial class SessionsView : UserControl
{
    public SessionsView() => InitializeComponent();

    /// <summary>
    /// Clicking a tab sets it as the active session so its ContentView is displayed.
    /// </summary>
    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: SessionTabViewModel tab }) return;
        if (DataContext is SessionsDockable vm)
            vm.ActiveSession = tab;
    }
}
