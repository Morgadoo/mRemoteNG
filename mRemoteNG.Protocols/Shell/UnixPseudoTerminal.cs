using System.Runtime.InteropServices;

namespace mRemoteNG.Protocols.Shell;

/// <summary>
/// A child process running on a real pseudo-terminal (Linux).
///
/// The master side stays in this process; the child is started with <c>posix_spawn</c> in a new session
/// (<c>POSIX_SPAWN_SETSID</c>) and opens the slave as stdin/stdout/stderr, which makes it the controlling
/// terminal. Programs therefore see a TTY (line editing, colours, <c>isatty</c>), the kernel line discipline
/// handles Enter (<c>\r</c>) and Ctrl+C, and <see cref="Resize"/> delivers <c>SIGWINCH</c> with the new size.
///
/// <c>posix_spawn</c> is used rather than <c>fork</c>, which is not safe in a multi-threaded .NET process.
/// The constants and structure sizes are glibc's/musl's on Linux, so this is Linux only.
/// </summary>
internal sealed class UnixPseudoTerminal : IDisposable
{
    private readonly int _masterFd;
    private readonly object _writeLock = new();
    private readonly object _reapLock = new();
    private Thread? _reader;
    private volatile bool _stopping;
    private int? _exitStatus;
    private bool _closed;

    private UnixPseudoTerminal(int masterFd, int pid)
    {
        _masterFd = masterFd;
        ProcessId = pid;
    }

    /// <summary>Whether a pseudo-terminal can be created on this platform.</summary>
    public static bool IsSupported => OperatingSystem.IsLinux();

    public int ProcessId { get; }

    /// <summary>True once the child has been reaped.</summary>
    public bool HasExited
    {
        get { lock (_reapLock) return _exitStatus.HasValue; }
    }

    /// <summary>
    /// Starts <paramref name="program"/> on a new pseudo-terminal of the given size.
    /// <paramref name="environment"/> is the complete environment of the child.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Not on Linux.</exception>
    /// <exception cref="IOException">The pseudo-terminal could not be created or the program not started.</exception>
    public static UnixPseudoTerminal Start(
        string program,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        int columns,
        int rows)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("Pseudo-terminals are only supported on Linux.");

        int master = Native.posix_openpt(Native.O_RDWR | Native.O_NOCTTY | Native.O_CLOEXEC);
        if (master < 0)
            throw Error("posix_openpt");

        try
        {
            if (Native.grantpt(master) != 0) throw Error("grantpt");
            if (Native.unlockpt(master) != 0) throw Error("unlockpt");

            var nameBuffer = new byte[256];
            int rc = Native.ptsname_r(master, nameBuffer, (nuint)nameBuffer.Length);
            if (rc != 0) throw new IOException($"ptsname_r failed (errno {rc}).");
            string slaveName = System.Text.Encoding.UTF8.GetString(nameBuffer, 0, Array.IndexOf(nameBuffer, (byte)0));

            SetWindowSize(master, columns, rows);
            int pid = Spawn(program, arguments, environment, slaveName);
            return new UnixPseudoTerminal(master, pid);
        }
        catch
        {
            Native.close(master);
            throw;
        }
    }

    private static int Spawn(string program, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, string slaveName)
    {
        // posix_spawn_file_actions_t / posix_spawnattr_t / sigset_t are opaque; these buffers are larger than
        // any Linux libc's definition (glibc: 80, 336 and 128 bytes).
        IntPtr fileActions = Marshal.AllocHGlobal(1024);
        IntPtr attributes = Marshal.AllocHGlobal(1024);
        IntPtr signals = Marshal.AllocHGlobal(1024);
        var strings = new List<IntPtr>();
        bool fileActionsReady = false, attributesReady = false;
        try
        {
            Check(Native.posix_spawn_file_actions_init(fileActions), "posix_spawn_file_actions_init");
            fileActionsReady = true;
            // Opening the slave after setsid() (and without O_NOCTTY) makes it the controlling terminal.
            Check(Native.posix_spawn_file_actions_addopen(fileActions, 0, slaveName, Native.O_RDWR, 0), "posix_spawn_file_actions_addopen");
            Check(Native.posix_spawn_file_actions_adddup2(fileActions, 0, 1), "posix_spawn_file_actions_adddup2");
            Check(Native.posix_spawn_file_actions_adddup2(fileActions, 0, 2), "posix_spawn_file_actions_adddup2");

            Check(Native.posix_spawnattr_init(attributes), "posix_spawnattr_init");
            attributesReady = true;
            Check(Native.posix_spawnattr_setflags(attributes,
                (short)(Native.POSIX_SPAWN_SETSID | Native.POSIX_SPAWN_SETSIGDEF | Native.POSIX_SPAWN_SETSIGMASK)), "posix_spawnattr_setflags");

            // The .NET runtime ignores SIGPIPE and handles others; give the child default dispositions
            // and an empty signal mask, as a terminal would.
            Native.sigfillset(signals);
            Native.sigdelset(signals, Native.SIGKILL);
            Native.sigdelset(signals, Native.SIGSTOP);
            Check(Native.posix_spawnattr_setsigdefault(attributes, signals), "posix_spawnattr_setsigdefault");
            Native.sigemptyset(signals);
            Check(Native.posix_spawnattr_setsigmask(attributes, signals), "posix_spawnattr_setsigmask");

            IntPtr[] argv = ToNative([program, .. arguments], strings);
            IntPtr[] envp = ToNative(environment.Select(kv => $"{kv.Key}={kv.Value}"), strings);

            int rc = program.Contains('/')
                ? Native.posix_spawn(out int pid, program, fileActions, attributes, argv, envp)
                : Native.posix_spawnp(out pid, program, fileActions, attributes, argv, envp);
            if (rc != 0)
                throw new IOException($"Could not start {program}: {new System.ComponentModel.Win32Exception(rc).Message} (errno {rc}).");
            return pid;
        }
        finally
        {
            if (fileActionsReady) Native.posix_spawn_file_actions_destroy(fileActions);
            if (attributesReady) Native.posix_spawnattr_destroy(attributes);
            Marshal.FreeHGlobal(fileActions);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(signals);
            foreach (var s in strings)
                Marshal.FreeCoTaskMem(s);
        }
    }

    private static IntPtr[] ToNative(IEnumerable<string> values, List<IntPtr> allocated)
    {
        var result = new List<IntPtr>();
        foreach (var value in values)
        {
            var ptr = Marshal.StringToCoTaskMemUTF8(value);
            allocated.Add(ptr);
            result.Add(ptr);
        }
        result.Add(IntPtr.Zero);
        return [.. result];
    }

    /// <summary>
    /// Starts the background reader. <paramref name="onData"/> receives output chunks (the buffer is reused);
    /// <paramref name="onExit"/> runs once the terminal closed, with the exit code if the child was reaped.
    /// </summary>
    public void StartReading(Action<byte[], int> onData, Action<int?> onExit)
    {
        if (_reader is not null)
            throw new InvalidOperationException("Already reading.");
        _reader = new Thread(() => ReadLoop(onData, onExit))
        {
            IsBackground = true,
            Name = $"pty-reader-{ProcessId}",
        };
        _reader.Start();
    }

    private void ReadLoop(Action<byte[], int> onData, Action<int?> onExit)
    {
        var buffer = new byte[16384];
        var pfd = new Native.PollFd { fd = _masterFd, events = Native.POLLIN };
        while (!_stopping)
        {
            pfd.revents = 0;
            int ready = Native.poll(ref pfd, 1, 100);
            if (ready < 0)
            {
                if (Marshal.GetLastPInvokeError() == Native.EINTR) continue;
                break;
            }
            if (ready == 0)
            {
                // A grandchild may keep the slave open after the shell itself exited.
                if (TryReap(wait: false)) break;
                continue;
            }
            if ((pfd.revents & Native.POLLIN) != 0)
            {
                nint read = Native.read(_masterFd, buffer, (nuint)buffer.Length);
                if (read > 0)
                {
                    onData(buffer, (int)read);
                    continue;
                }
                if (read < 0 && Marshal.GetLastPInvokeError() is Native.EINTR or Native.EAGAIN) continue;
                break; // EIO: every slave descriptor is closed
            }
            if ((pfd.revents & (Native.POLLHUP | Native.POLLERR | Native.POLLNVAL)) != 0)
                break;
        }

        if (!_stopping)
        {
            // The slave closed: the child is exiting. Give it a moment to be reaped.
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!TryReap(wait: false) && DateTime.UtcNow < deadline)
                Thread.Sleep(20);
        }
        int? status;
        lock (_reapLock) status = _exitStatus;
        onExit(status is int s ? DecodeExitCode(s) : null);
    }

    private static int DecodeExitCode(int status) =>
        (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);

    private bool TryReap(bool wait)
    {
        lock (_reapLock)
        {
            if (_exitStatus.HasValue) return true;
            int rc = Native.waitpid(ProcessId, out int status, wait ? 0 : Native.WNOHANG);
            if (rc == ProcessId)
            {
                _exitStatus = status;
                return true;
            }
            if (rc < 0 && Marshal.GetLastPInvokeError() == Native.ECHILD)
            {
                _exitStatus = 0; // already reaped elsewhere
                return true;
            }
            return false;
        }
    }

    /// <summary>Writes input to the terminal as if typed.</summary>
    /// <exception cref="IOException">The terminal is closed.</exception>
    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_writeLock)
        {
            if (_closed) throw new ObjectDisposedException(nameof(UnixPseudoTerminal));
            var array = data.ToArray();
            int offset = 0;
            while (offset < array.Length)
            {
                nint written = Native.write(_masterFd, array, offset, (nuint)(array.Length - offset));
                if (written < 0)
                {
                    int errno = Marshal.GetLastPInvokeError();
                    if (errno == Native.EINTR) continue;
                    throw new IOException($"write to pseudo-terminal failed (errno {errno}).");
                }
                offset += (int)written;
            }
        }
    }

    /// <summary>Sets the terminal size; the foreground process receives SIGWINCH.</summary>
    public void Resize(int columns, int rows, int pixelWidth = 0, int pixelHeight = 0)
    {
        lock (_writeLock)
        {
            if (_closed) return;
            SetWindowSize(_masterFd, columns, rows, pixelWidth, pixelHeight);
        }
    }

    private static void SetWindowSize(int fd, int columns, int rows, int pixelWidth = 0, int pixelHeight = 0)
    {
        var size = new Native.WinSize
        {
            ws_col = (ushort)Math.Clamp(columns, 1, ushort.MaxValue),
            ws_row = (ushort)Math.Clamp(rows, 1, ushort.MaxValue),
            ws_xpixel = (ushort)Math.Clamp(pixelWidth, 0, ushort.MaxValue),
            ws_ypixel = (ushort)Math.Clamp(pixelHeight, 0, ushort.MaxValue),
        };
        if (Native.ioctl(fd, Native.TIOCSWINSZ, ref size) != 0)
            throw Error("ioctl(TIOCSWINSZ)");
    }

    /// <summary>Waits until the child has been reaped.</summary>
    public bool WaitForExit(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!TryReap(wait: false))
        {
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(20);
        }
        return true;
    }

    /// <summary>Hangs up the session (SIGHUP to the child's process group), then kills it if it lingers.</summary>
    public void Terminate(TimeSpan grace)
    {
        if (TryReap(wait: false)) return;
        Native.kill(-ProcessId, Native.SIGHUP);
        Native.kill(ProcessId, Native.SIGHUP);
        if (WaitForExit(grace)) return;
        Native.kill(-ProcessId, Native.SIGKILL);
        Native.kill(ProcessId, Native.SIGKILL);
        WaitForExit(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        Terminate(TimeSpan.FromSeconds(1));
        _stopping = true;
        if (_reader is not null && _reader != Thread.CurrentThread)
            _reader.Join(TimeSpan.FromSeconds(2));
        lock (_writeLock)
        {
            if (_closed) return;
            _closed = true;
            Native.close(_masterFd);
        }
    }

    private static IOException Error(string call)
    {
        int errno = Marshal.GetLastPInvokeError();
        return new IOException($"{call} failed: {new System.ComponentModel.Win32Exception(errno).Message} (errno {errno}).");
    }

    private static void Check(int rc, string call)
    {
        if (rc != 0)
            throw new IOException($"{call} failed (errno {rc}).");
    }

    private static class Native
    {
        private const string Libc = "libc";

        public const int O_RDWR = 0x2;
        public const int O_NOCTTY = 0x100;
        public const int O_CLOEXEC = 0x80000;
        public const ulong TIOCSWINSZ = 0x5414;
        public const int POSIX_SPAWN_SETSIGDEF = 0x04;
        public const int POSIX_SPAWN_SETSIGMASK = 0x08;
        public const int POSIX_SPAWN_SETSID = 0x80;
        public const int SIGHUP = 1;
        public const int SIGKILL = 9;
        public const int SIGSTOP = 19;
        public const int WNOHANG = 1;
        public const int EINTR = 4;
        public const int EAGAIN = 11;
        public const int ECHILD = 10;
        public const short POLLIN = 0x1;
        public const short POLLERR = 0x8;
        public const short POLLHUP = 0x10;
        public const short POLLNVAL = 0x20;

        [StructLayout(LayoutKind.Sequential)]
        public struct WinSize
        {
            public ushort ws_row;
            public ushort ws_col;
            public ushort ws_xpixel;
            public ushort ws_ypixel;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PollFd
        {
            public int fd;
            public short events;
            public short revents;
        }

        [DllImport(Libc, SetLastError = true)] public static extern int posix_openpt(int flags);
        [DllImport(Libc, SetLastError = true)] public static extern int grantpt(int fd);
        [DllImport(Libc, SetLastError = true)] public static extern int unlockpt(int fd);
        [DllImport(Libc)] public static extern int ptsname_r(int fd, byte[] buffer, nuint length);
        [DllImport(Libc, SetLastError = true)] public static extern int close(int fd);
        [DllImport(Libc, SetLastError = true)] public static extern nint read(int fd, byte[] buffer, nuint count);
        [DllImport(Libc, SetLastError = true, EntryPoint = "write")] private static extern nint write(int fd, ref byte buffer, nuint count);
        [DllImport(Libc, SetLastError = true)] public static extern int ioctl(int fd, ulong request, ref WinSize size);
        [DllImport(Libc, SetLastError = true)] public static extern int poll(ref PollFd fds, nuint count, int timeout);
        [DllImport(Libc, SetLastError = true)] public static extern int waitpid(int pid, out int status, int options);
        [DllImport(Libc, SetLastError = true)] public static extern int kill(int pid, int signal);

        [DllImport(Libc)] public static extern int sigemptyset(IntPtr set);
        [DllImport(Libc)] public static extern int sigfillset(IntPtr set);
        [DllImport(Libc)] public static extern int sigdelset(IntPtr set, int signal);

        [DllImport(Libc)] public static extern int posix_spawn_file_actions_init(IntPtr actions);
        [DllImport(Libc)] public static extern int posix_spawn_file_actions_destroy(IntPtr actions);
        [DllImport(Libc)] public static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
        [DllImport(Libc)] public static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int newFd);
        [DllImport(Libc)] public static extern int posix_spawnattr_init(IntPtr attributes);
        [DllImport(Libc)] public static extern int posix_spawnattr_destroy(IntPtr attributes);
        [DllImport(Libc)] public static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);
        [DllImport(Libc)] public static extern int posix_spawnattr_setsigdefault(IntPtr attributes, IntPtr signals);
        [DllImport(Libc)] public static extern int posix_spawnattr_setsigmask(IntPtr attributes, IntPtr signals);
        [DllImport(Libc)] public static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr actions, IntPtr attributes, IntPtr[] argv, IntPtr[] envp);
        [DllImport(Libc)] public static extern int posix_spawnp(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string file, IntPtr actions, IntPtr attributes, IntPtr[] argv, IntPtr[] envp);

        public static nint write(int fd, byte[] buffer, int offset, nuint count) =>
            write(fd, ref buffer[offset], count);
    }
}
