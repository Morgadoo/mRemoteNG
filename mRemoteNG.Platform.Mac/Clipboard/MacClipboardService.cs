using System.Diagnostics;

namespace mRemoteNG.Platform.Mac.Clipboard;

/// <summary>
/// macOS clipboard service using pbcopy/pbpaste command-line tools.
/// These tools are available on all macOS versions without additional installation.
/// </summary>
public sealed class MacClipboardService : IClipboardService
{
    public string? GetText()
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = "pbpaste",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return null;
            var text = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0 ? text : null;
        }
        catch { return null; }
    }

    public void SetText(string text)
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = "pbcopy",
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return;
            proc.StandardInput.Write(text);
            proc.StandardInput.Close();
            proc.WaitForExit();
        }
        catch { }
    }

    public void Clear() => SetText(string.Empty);
    public bool ContainsText() => GetText() is not null;
}
