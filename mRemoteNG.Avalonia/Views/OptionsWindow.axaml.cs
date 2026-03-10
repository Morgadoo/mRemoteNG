using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views;

public partial class OptionsWindow : Window
{
    public OptionsWindow()
    {
        InitializeComponent();
        var vm = AppServices.GetRequired<OptionsWindowViewModel>();
        DataContext = vm;
        vm.CloseRequested += () => Close();
    }
}
