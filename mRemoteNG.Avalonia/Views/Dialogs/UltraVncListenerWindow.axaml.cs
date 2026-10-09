using Avalonia.Controls;
using mRemoteNG.Avalonia.ViewModels;

namespace mRemoteNG.Avalonia.Views.Dialogs;

/// <summary>
/// Non-modal window for the UltraVNC SingleClick listener. Listening starts when the window opens and stops when it
/// closes; only one window exists at a time (see <see cref="ShowOrActivate"/>).
/// </summary>
public partial class UltraVncListenerWindow : Window
{
    private static UltraVncListenerWindow? _instance;

    public UltraVncListenerWindow() : this(null)
    {
    }

    public UltraVncListenerWindow(UltraVncListenerViewModel? viewModel, bool startListening = true)
    {
        InitializeComponent();
        var vm = viewModel ?? UltraVncListenerViewModel.CreateForApp();
        DataContext = vm;
        if (startListening)
            Opened += (_, _) => vm.Start();
        Closed += async (_, _) =>
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
            await vm.StopAsync();
        };
    }

    public UltraVncListenerViewModel ViewModel => (UltraVncListenerViewModel)DataContext!;

    /// <summary>Shows the listener window (creating it on first use) for Tools ▸ UltraVNC SingleClick listener.</summary>
    public static UltraVncListenerWindow ShowOrActivate(Window? owner)
    {
        if (_instance is { } existing)
        {
            existing.Activate();
            return existing;
        }
        _instance = new UltraVncListenerWindow(null);
        if (owner is not null)
            _instance.Show(owner);
        else
            _instance.Show();
        return _instance;
    }
}
