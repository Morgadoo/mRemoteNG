using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace mRemoteNG.Avalonia.Views.Dialogs;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = VersionString(typeof(AboutDialog).Assembly);
        RuntimeText.Text = $"{RuntimeInformation.FrameworkDescription} · Avalonia UI {VersionString(typeof(Window).Assembly)}";

        OkButton.Click += (_, _) => Close();
        GitHubButton.Click += (_, _) =>
            Process.Start(new ProcessStartInfo("https://github.com/mRemoteNG/mRemoteNG") { UseShellExecute = true });
        DocsButton.Click += (_, _) =>
            Process.Start(new ProcessStartInfo("https://mremoteng.readthedocs.io") { UseShellExecute = true });
    }

    /// <summary>The informational version without the build metadata ("1.78.2-dev", not "1.78.2-dev+3f2a…").</summary>
    private static string VersionString(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? string.Empty;
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}
