using Avalonia.Controls;

namespace mRemoteNG.Avalonia.Views.Shell;

/// <summary>
/// The session area's empty state: icon, title, "Quick connect" and "New connection", keyboard hints and up to eight
/// recent / favourite connection cards (click connects).
/// </summary>
public partial class SessionsEmptyState : UserControl
{
    public SessionsEmptyState()
    {
        InitializeComponent();
    }
}
