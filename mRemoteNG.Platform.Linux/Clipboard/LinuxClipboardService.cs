using System.Diagnostics;

namespace mRemoteNG.Platform.Linux.Clipboard;

/// <summary>
/// Linux clipboard service using external tools.
/// Supports both X11 (xclip) and Wayland (wl-clipboard) environments.
/// Detection order: WAYLAND_DISPLAY → wl-copy/wl-paste; fallback → xclip.
/// </summary>
public sealed class LinuxClipboardService : IClipboardService
{
    private readonly bool _isWayland;

    public LinuxClipboardService()
    {
        _isWayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    public string? GetText()
    {
        try
        {
            var (tool, args) = _isWayland
                ? ("wl-paste", "--no-newline")
                : ("xclip", "-selection clipboard -o");

            using var proc = System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = tool,
                Arguments = args,
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
            var (tool, args) = _isWayland
                ? ("wl-copy", string.Empty)
                : ("xclip", "-selection clipboard");

            using var proc = System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName = tool,
                Arguments = args,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (proc is null) return;
            proc.StandardInput.Write(text);
            proc.StandardInput.Close();
            proc.WaitForExit();
        }
        catch { /* Clipboard not available — ignore */ }
    }

    public void Clear() => SetText(string.Empty);

    public bool ContainsText() => GetText() is not null;
}
