using Avalonia.Input;
using mRemoteNG.Avalonia.ViewModels.Docking;

namespace mRemoteNG.Avalonia.Views.Sessions;

/// <summary>
/// Session navigation keys, handled in the tunnelling phase so they work while a terminal or
/// remote desktop has the keyboard: Ctrl+Tab / Ctrl+Shift+Tab (next / previous tab of the active
/// panel) and Ctrl+1…9 (jump to tab n).
/// </summary>
public static class SessionKeyboard
{
    public static bool Handle(SessionsDockable sessions, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Control)
        {
            sessions.SelectAdjacentSession(+1);
            return true;
        }
        if (e.Key == Key.Tab && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            sessions.SelectAdjacentSession(-1);
            return true;
        }
        if (e.KeyModifiers == KeyModifiers.Control && SessionNumber(e.Key) is { } number)
            return sessions.SelectSessionNumber(number);
        return false;
    }

    private static int? SessionNumber(Key key) => key switch
    {
        >= Key.D1 and <= Key.D9 => key - Key.D1 + 1,
        >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1 + 1,
        _ => null,
    };
}
