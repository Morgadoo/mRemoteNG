namespace mRemoteNG.Platform;

/// <summary>
/// Abstracts clipboard access across Windows, Linux, and macOS.
/// On Windows: uses Win32 clipboard APIs.
/// On Linux: uses xclip or wl-clipboard subprocess.
/// On macOS: uses pbcopy/pbpaste subprocess.
/// </summary>
public interface IClipboardService
{
    /// <summary>Returns the current clipboard text, or null if clipboard contains no text.</summary>
    string? GetText();

    /// <summary>Sets the clipboard to the provided text.</summary>
    void SetText(string text);

    /// <summary>Clears the clipboard.</summary>
    void Clear();

    /// <summary>Returns true if the clipboard currently contains text data.</summary>
    bool ContainsText();
}
