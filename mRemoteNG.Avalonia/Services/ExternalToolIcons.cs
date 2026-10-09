using Avalonia.Media.Imaging;
using Material.Icons;
using mRemoteNG.Core.Tools;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Icons for external tools: the tool's own image file when set and loadable (<see cref="GetCustom"/>), otherwise a
/// Material glyph chosen from what the tool does (<see cref="KindFor"/>).
/// </summary>
public static class ExternalToolIcons
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    /// <summary>The tool's own image, or null when it has none (or it cannot be loaded).</summary>
    public static Bitmap? GetCustom(ExternalTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return string.IsNullOrWhiteSpace(tool.IconPath) ? null : LoadFile(tool.IconPath);
    }

    /// <summary>A glyph for a tool without its own image, guessed from its program, arguments and name.</summary>
    public static MaterialIconKind KindFor(ExternalTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        string text = $"{tool.FileName} {tool.Arguments} {tool.DisplayName}".ToLowerInvariant();
        if (text.Contains("powershell") || text.Contains("pwsh"))
            return MaterialIconKind.Powershell;
        if (text.Contains("ssh"))
            return MaterialIconKind.KeyVariant;
        if (text.Contains("ping") || text.Contains("trace") || text.Contains("tracert") || text.Contains("mtr"))
            return MaterialIconKind.Radar;
        if (text.Contains("calc"))
            return MaterialIconKind.Calculator;
        if (text.Contains("http") || text.Contains("browser") || text.Contains("firefox") || text.Contains("chrome"))
            return MaterialIconKind.Web;
        if (tool.TryIntegrate || text.Contains("term") || text.Contains("cmd") || text.Contains("konsole"))
            return MaterialIconKind.Console;
        return MaterialIconKind.ApplicationOutline;
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
