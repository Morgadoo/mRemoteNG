using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using mRemoteNG.Avalonia.Views;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia;

public partial class App : Application
{
    public override void Initialize() =>
        AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = AppServices.GetRequired<MainWindowViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
