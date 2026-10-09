using System.Text;

namespace mRemoteNG.Platform.Settings;

/// <summary>
/// Writes files atomically: the content goes to a temporary file in the same directory,
/// is flushed to disk, and then renamed over the target. A crash mid-write therefore
/// leaves either the old file or the new file, never a truncated one.
/// </summary>
public static class AtomicFile
{
    /// <summary>Owner read/write only (chmod 600).</summary>
    public const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Atomically replaces <paramref name="path"/> with <paramref name="contents"/> (UTF-8, no BOM).</summary>
    /// <param name="path">Target file.</param>
    /// <param name="contents">Text to write.</param>
    /// <param name="unixMode">
    /// Permissions for the new file on Unix. The file is created with these permissions,
    /// so it is never readable by others, even briefly. Ignored on Windows.
    /// </param>
    public static void WriteAllText(string path, string contents, UnixFileMode? unixMode = null) =>
        WriteAllBytes(path, Utf8NoBom.GetBytes(contents), unixMode);

    /// <inheritdoc cref="WriteAllText"/>
    public static void WriteAllBytes(string path, byte[] contents, UnixFileMode? unixMode = null)
    {
        var tempPath = WriteTemp(path, contents, unixMode);
        try
        {
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> only if no file exists there yet.
    /// Returns false (and leaves the existing file untouched) when another writer got there first.
    /// </summary>
    public static bool TryCreateNew(string path, byte[] contents, UnixFileMode? unixMode = null)
    {
        var tempPath = WriteTemp(path, contents, unixMode);
        try
        {
            File.Move(tempPath, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            TryDelete(tempPath);
            return false;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static string WriteTemp(string path, byte[] contents, UnixFileMode? unixMode)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException($"'{path}' has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (unixMode is { } mode && !OperatingSystem.IsWindows())
            options.UnixCreateMode = mode;

        try
        {
            using var stream = new FileStream(tempPath, options);
            stream.Write(contents, 0, contents.Length);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        return tempPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort: a leftover temp file is harmless.
        }
    }
}
