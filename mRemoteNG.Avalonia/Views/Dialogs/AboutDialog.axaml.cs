using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Diagnostics;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        OkButton.Click += (_, _) => Close();
        GitHubButton.Click += (_, _) =>
            Process.Start(new ProcessStartInfo("https://github.com/mRemoteNG/mRemoteNG") { UseShellExecute = true });
        DocsButton.Click += (_, _) =>
            Process.Start(new ProcessStartInfo("https://mremoteng.readthedocs.io") { UseShellExecute = true });
    }
}
