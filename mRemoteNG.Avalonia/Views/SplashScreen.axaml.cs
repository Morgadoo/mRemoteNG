using Avalonia.Controls;
using Avalonia.Threading;

namespace mRemoteNG.Avalonia.Views;

/// <summary>
/// Splash screen shown during async initialization.
/// Call <see cref="UpdateProgress"/> from the initialization sequence.
/// </summary>
public partial class SplashScreen : Window
{
    public SplashScreen()
    {
        InitializeComponent();
    }

    public void UpdateProgress(double percent, string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ProgressBar.Value = percent;
            StatusText.Text = status;
        });
    }

    public async Task RunInitialization(Func<Action<double, string>, Task> initAction)
    {
        Show();
        try
        {
            await initAction((pct, msg) => UpdateProgress(pct, msg));
        }
        finally
        {
            Close();
        }
    }
}
