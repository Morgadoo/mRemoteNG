using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace mRemoteNG.Platform.Windows.Process;

/// <summary>
/// Windows implementation of <see cref="IProcessService"/> using Job Objects.
/// All child processes added via <see cref="TrackChildProcess"/> are automatically
/// terminated when the parent process exits — even after a crash.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessService : IProcessService, IDisposable
{
    private readonly IntPtr _jobHandle;
    private bool _disposed;

    public WindowsProcessService()
    {
        _jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_jobHandle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create Job Object.");

        // Configure: terminate all job processes when the last handle closes.
        var info = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            LimitFlags = 0x2000 // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };
        var extInfo = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = info };
        int length = Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        var extInfoPtr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(extInfo, extInfoPtr, false);
            if (!NativeMethods.SetInformationJobObject(_jobHandle,
                    NativeMethods.JobObjectInfoType.ExtendedLimitInformation, extInfoPtr, (uint)length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to configure Job Object.");
        }
        finally
        {
            Marshal.FreeHGlobal(extInfoPtr);
        }
    }

    public void TrackChildProcess(System.Diagnostics.Process process)
    {
        if (!NativeMethods.AssignProcessToJobObject(_jobHandle, process.Handle))
        {
            // Process may already be in a job (common in some environments).
            // Non-fatal: log and continue.
            System.Diagnostics.Trace.TraceWarning(
                $"Could not assign process {process.Id} to Job Object: {Marshal.GetLastWin32Error()}");
        }
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

    public IntPtr GetNativeHandle(System.Diagnostics.Process process) => process.MainWindowHandle;

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_jobHandle != IntPtr.Zero)
                NativeMethods.CloseHandle(_jobHandle);
            _disposed = true;
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType infoType,
            IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr hObject);

        internal enum JobObjectInfoType { ExtendedLimitInformation = 9 }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
