namespace mRemoteNG.Platform;

/// <summary>
/// Abstracts child-process lifecycle management across platforms.
/// On Windows: uses Win32 Job Objects to track child processes.
/// On Linux: uses prctl(PR_SET_PDEATHSIG) to ensure child cleanup.
/// On macOS: uses kqueue/POSIX signals.
/// </summary>
public interface IProcessService
{
    /// <summary>
    /// Registers a process so that it is automatically terminated
    /// when the calling process exits.
    /// </summary>
    void TrackChildProcess(System.Diagnostics.Process process);

    /// <summary>
    /// Spawns a child process with the given start info.
    /// The process is automatically tracked for cleanup.
    /// </summary>
    System.Diagnostics.Process StartTracked(System.Diagnostics.ProcessStartInfo startInfo);

    /// <summary>
    /// Kills a previously spawned process and all its children.
    /// </summary>
    void KillProcessTree(System.Diagnostics.Process process);

    /// <summary>
    /// Returns the native handle (HWND on Windows, PID on Linux/macOS)
    /// for a running process, suitable for window embedding.
    /// </summary>
    IntPtr GetNativeHandle(System.Diagnostics.Process process);
}
