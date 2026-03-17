using System.Runtime.Versioning;
using System.Windows.Forms;

namespace mRemoteNG.Platform.Windows.Clipboard;

/// <summary>
/// Windows clipboard implementation using System.Windows.Forms.Clipboard.
/// Must be called from an STA thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardService : IClipboardService
{
    public string? GetText() =>
        System.Windows.Forms.Clipboard.ContainsText()
            ? System.Windows.Forms.Clipboard.GetText()
            : null;

    public void SetText(string text) =>
        System.Windows.Forms.Clipboard.SetText(text);

    public void Clear() =>
        System.Windows.Forms.Clipboard.Clear();

    public bool ContainsText() =>
        System.Windows.Forms.Clipboard.ContainsText();
}
