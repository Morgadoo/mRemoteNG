using Avalonia;
using Avalonia.Controls;

namespace mRemoteNG.Avalonia.Views;

/// <summary>Puts text on the window clipboard. Does nothing when the control is not on a window yet.</summary>
internal static class TextClipboard
{
    public static async Task SetAsync(Visual source, string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var clipboard = TopLevel.GetTopLevel(source)?.Clipboard;
        if (clipboard is null)
            return;

        try
        {
            await clipboard.SetTextAsync(text);
        }
        catch (Exception)
        {
            // Best effort: opening the clipboard can time out while another application holds it.
        }
    }
}
