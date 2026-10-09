namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Detects another running instance through an exclusive lock on a file in the settings
/// directory. The OS releases the lock when the process exits, even after a crash.
/// Every instance takes the lock when it can, so the "single instance" option works
/// regardless of which instance had it enabled.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private FileStream? _lock;

    /// <summary>Tries to become the primary instance. Returns false when another instance holds the lock.</summary>
    public bool TryAcquire(string directory)
    {
        if (_lock is not null)
            return true;

        try
        {
            Directory.CreateDirectory(directory);
            _lock = new FileStream(Path.Combine(directory, "instance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Cannot lock (read-only directory): do not block startup.
            return true;
        }
    }

    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
    }
}
