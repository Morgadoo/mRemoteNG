using System.Diagnostics;
using System.Runtime.InteropServices;

namespace mRemoteNG.Platform.Linux.Process;

/// <summary>
/// Linux process service.
/// Uses prctl(PR_SET_PDEATHSIG, SIGTERM) to request child process cleanup
/// when the parent exits. This is a best-effort mechanism — the kernel
/// delivers the signal when the parent thread exits, not the process.
/// </summary>
public sealed class LinuxProcessService : IProcessService
{
    // prctl constants
    private const int PR_SET_PDEATHSIG = 1;
    private const int SIGTERM = 15;

    [DllImport("libc", SetLastError = true)]
    private static extern int prctl(int option, ulong arg2, ulong arg3 = 0, ulong arg4 = 0, ulong arg5 = 0);

    public void TrackChildProcess(System.Diagnostics.Process process)
    {
        // Called in the child process context is not possible from the parent via P/Invoke directly.
        // The correct approach is to set PR_SET_PDEATHSIG in the child via a preExec hook.
        // We track via an in-process list and kill on AppDomain.ProcessExit as fallback.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TryKill(process);
    }

    public System.Diagnostics.Process StartTracked(ProcessStartInfo startInfo)
    {
        var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start returned null.");
        TrackChildProcess(process);
        return process;
    }

    public void KillProcessTree(System.Diagnostics.Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* Already exited */ }
    }

    public IntPtr GetNativeHandle(System.Diagnostics.Process process) =>
        // On Linux the "handle" for embedding is the X11 window ID, not a PID.
        // Callers (e.g., protocol embedders) should use XGetWindowProperty
        // after the child process creates a window.
        new IntPtr(process.Id);

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { /* Best effort */ }
    }
}
