using Avalonia.Media.Imaging;
using mRemoteNG.Core.Tools;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Icons for external tools: the tool's own image file when set and loadable, otherwise a built-in icon chosen from
/// what the tool does (terminal, integrated window, generic program).
/// </summary>
public static class ExternalToolIcons
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    public static Bitmap? Get(ExternalTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!string.IsNullOrWhiteSpace(tool.IconPath) && LoadFile(tool.IconPath) is { } custom)
            return custom;
        return IconService.LoadIcon(DefaultIconName(tool));
    }

    internal static string DefaultIconName(ExternalTool tool)
    {
        string text = $"{tool.FileName} {tool.Arguments} {tool.DisplayName}".ToLowerInvariant();
        if (text.Contains("powershell") || text.Contains("pwsh"))
            return "PowerShell.ico";
        if (text.Contains("ssh"))
            return "SSH.ico";
        if (text.Contains("ping") || text.Contains("trace") || text.Contains("tracert"))
            return "Router.ico";
        if (tool.TryIntegrate || text.Contains("term") || text.Contains("cmd") || text.Contains("konsole"))
            return "Console.ico";
        return "Admin.ico";
    }

    private static Bitmap? LoadFile(string path)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached))
                return cached;
            Bitmap? bitmap = null;
            try
            {
                if (File.Exists(path))
                    bitmap = new Bitmap(path);
            }
            catch (Exception)
            {
                // Not an image Avalonia can decode: use the default icon.
            }
            Cache[path] = bitmap;
            return bitmap;
        }
    }
}
