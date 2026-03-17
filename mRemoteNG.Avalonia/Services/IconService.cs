using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Provides protocol and connection type icons as Avalonia bitmaps.
/// Icons are embedded in the application's Assets/Icons/ folder.
/// Falls back to a default icon if a specific one is not found.
/// </summary>
public static class IconService
{
    private static readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> ProtocolIconMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RDP"]       = "Remote Desktop.ico",
        ["SSH"]       = "SSH.ico",
        ["SSH1"]      = "SSH.ico",
        ["SSH2"]      = "SSH.ico",
        ["VNC"]       = "Virtual Machine.ico",
        ["Telnet"]    = "Telnet.ico",
        ["Rlogin"]    = "Tel.ico",
        ["RAW"]       = "Console.ico",
        ["HTTP"]      = "Web Server.ico",
        ["HTTPS"]     = "Web Server.ico",
        ["PowerShell"]= "PowerShell.ico",
        ["WSL"]       = "WSL.ico",
        ["ARD"]       = "Apple.ico",
        ["AnyDesk"]   = "Remote Desktop.ico",
        ["Serial"]    = "Console.ico",
        ["Terminal"]  = "Console.ico",
        ["Folder"]    = "Infrastructure.ico",
    };

    /// <summary>Returns the icon for a given protocol name.</summary>
    public static Bitmap? GetProtocolIcon(string protocol)
    {
        if (ProtocolIconMap.TryGetValue(protocol, out var fileName))
            return LoadIcon(fileName);
        return LoadIcon("mRemoteNG.ico");
    }

    /// <summary>Loads an icon by filename from Assets/Icons/.</summary>
    public static Bitmap? LoadIcon(string fileName)
    {
        if (_cache.TryGetValue(fileName, out var cached)) return cached;

        try
        {
            var uri = new Uri($"avares://mRemoteNG.Avalonia/Assets/Icons/{fileName}");
            var bitmap = new Bitmap(AssetLoader.Open(uri));
            _cache[fileName] = bitmap;
            return bitmap;
        }
        catch
        {
            _cache[fileName] = null;
            return null;
        }
    }

    /// <summary>Returns the application window icon.</summary>
    public static WindowIcon? GetAppIcon()
    {
        try
        {
            var uri = new Uri("avares://mRemoteNG.Avalonia/Assets/Icons/mRemoteNG.ico");
            return new WindowIcon(AssetLoader.Open(uri));
        }
        catch { return null; }
    }
}
