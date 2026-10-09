using Avalonia.Controls;

namespace mRemoteNG.Avalonia.Services;

/// <summary>Opens files and folders with the desktop's default application (log file, downloaded updates).</summary>
public static class FileLauncher
{
    /// <summary>Opens <paramref name="path"/> (e.g. the log file in a text editor). Returns false when it could not.</summary>
    public static async Task<bool> OpenFileAsync(TopLevel? topLevel, string path)
    {
        if (topLevel is null || !File.Exists(path))
            return false;
        var file = await topLevel.StorageProvider.TryGetFileFromPathAsync(new Uri(Path.GetFullPath(path)));
        return file is not null && await topLevel.Launcher.LaunchFileAsync(file);
    }

    /// <summary>Opens the folder containing <paramref name="path"/> (or the folder itself).</summary>
    public static async Task<bool> RevealAsync(TopLevel? topLevel, string path)
    {
        if (topLevel is null)
            return false;
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            return false;
        Directory.CreateDirectory(directory);
        var folder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(directory)));
        return folder is not null && await topLevel.Launcher.LaunchFileAsync(folder);
    }
}
