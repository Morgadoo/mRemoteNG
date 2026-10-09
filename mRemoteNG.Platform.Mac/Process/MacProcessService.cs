using System.Diagnostics;

namespace mRemoteNG.Platform.Mac.Process;

/// <summary>
/// macOS process service.
/// Uses AppDomain.ProcessExit for child cleanup.
/// For deeper integration, kqueue-based process monitoring can be added.
/// </summary>
public sealed class MacProcessService : IProcessService
{
    public void TrackChildProcess(System.Diagnostics.Process process)
    {
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
        catch (InvalidOperationException) { }
    }

    public IntPtr GetNativeHandle(System.Diagnostics.Process process) =>
        // On macOS, the NSView/NSWindow handle for embedding requires ObjC interop.
        // Return the process ID; callers will use macOS-specific embedding APIs.
        new IntPtr(process.Id);

    private static void TryKill(System.Diagnostics.Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }
}
