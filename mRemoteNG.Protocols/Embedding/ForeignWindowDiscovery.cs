using System.Globalization;

namespace mRemoteNG.Protocols.Embedding;

/// <summary>What is known about one X11 window while looking for the window of a launched program.</summary>
internal readonly record struct WindowCandidate(
    nint Window,
    int? Pid,
    bool HasWmState,
    bool IsViewable,
    bool OverrideRedirect,
    bool IsRootChild,
    int Width,
    int Height,
    string? WmClassInstance = null,
    string? WmClassName = null);

/// <summary>
/// Platform-neutral part of finding a launched program's top-level window: which processes belong to the program
/// (it may fork helpers or exec through a wrapper) and which of the windows they own is the main one.
/// </summary>
internal static class ForeignWindowDiscovery
{
    /// <summary>
    /// <paramref name="rootPid"/> and all of its descendants, given (pid, parent pid) pairs of the running processes.
    /// </summary>
    public static HashSet<int> GetProcessTree(int rootPid, IEnumerable<(int Pid, int ParentPid)> processes)
    {
        var children = processes
            .GroupBy(p => p.ParentPid)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Pid).ToList());

        var result = new HashSet<int> { rootPid };
        var pending = new Queue<int>();
        pending.Enqueue(rootPid);
        while (pending.Count > 0)
        {
            int pid = pending.Dequeue();
            if (!children.TryGetValue(pid, out var kids))
                continue;
            foreach (int kid in kids)
            {
                if (result.Add(kid))
                    pending.Enqueue(kid);
            }
        }
        return result;
    }

    /// <summary>
    /// Picks the program's main window: a visible, non-override-redirect top-level window (one the window manager
    /// manages — it has WM_STATE — or, without a window manager, a direct child of the root) whose _NET_WM_PID is one
    /// of <paramref name="pids"/>. Windows of <paramref name="rootPid"/> itself win, then the largest. 0 when none.
    /// </summary>
    public static nint SelectWindow(IEnumerable<WindowCandidate> candidates, IReadOnlySet<int> pids, int rootPid)
    {
        return candidates
            .Where(c => c.Pid is { } pid && pids.Contains(pid))
            .Where(c => c.IsViewable && !c.OverrideRedirect && (c.HasWmState || c.IsRootChild))
            .Where(c => c.Width > 1 && c.Height > 1)
            .OrderByDescending(c => c.Pid == rootPid)
            .ThenByDescending(c => (long)c.Width * c.Height)
            .Select(c => c.Window)
            .FirstOrDefault();
    }

    /// <summary>
    /// Fallback for programs that do not set _NET_WM_PID (e.g. Xt/Motif programs such as xcalc) when the X server
    /// cannot tell the owning process either: a new window (not in <paramref name="preexisting"/>) without a pid whose
    /// WM_CLASS instance or class is the name of one of the launched processes (<paramref name="processNames"/>).
    /// 0 when none.
    /// </summary>
    public static nint SelectWindowByClass(IEnumerable<WindowCandidate> candidates, IReadOnlySet<nint> preexisting,
        IReadOnlyCollection<string> processNames)
    {
        bool Matches(string? value) =>
            !string.IsNullOrEmpty(value) && processNames.Any(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase));

        return candidates
            .Where(c => c.Pid is null && !preexisting.Contains(c.Window))
            .Where(c => c.IsViewable && !c.OverrideRedirect && (c.HasWmState || c.IsRootChild))
            .Where(c => c.Width > 1 && c.Height > 1)
            .Where(c => Matches(c.WmClassInstance) || Matches(c.WmClassName))
            .OrderByDescending(c => (long)c.Width * c.Height)
            .Select(c => c.Window)
            .FirstOrDefault();
    }

    /// <summary>Process names (Linux <c>/proc/&lt;pid&gt;/comm</c>) of <paramref name="pids"/>; empty elsewhere.</summary>
    public static List<string> ReadLinuxProcessNames(IEnumerable<int> pids)
    {
        var result = new List<string>();
        if (!OperatingSystem.IsLinux())
            return result;
        foreach (int pid in pids)
        {
            try
            {
                string name = File.ReadAllText($"/proc/{pid}/comm").Trim();
                if (name.Length > 0)
                    result.Add(name);
            }
            catch (IOException)
            {
                // The process exited meanwhile.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return result;
    }

    /// <summary>Parses the parent pid out of a Linux <c>/proc/&lt;pid&gt;/stat</c> line ("pid (comm) state ppid …").</summary>
    public static bool TryParseProcStat(string stat, out int pid, out int parentPid)
    {
        pid = parentPid = 0;
        int open = stat.IndexOf('(');
        int close = stat.LastIndexOf(')'); // the command name may itself contain ')' or spaces
        if (open <= 0 || close < open)
            return false;
        if (!int.TryParse(stat.AsSpan(0, open).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out pid))
            return false;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 2 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out parentPid);
    }

    /// <summary>(pid, parent pid) of every process, from /proc (Linux only; empty elsewhere).</summary>
    public static List<(int Pid, int ParentPid)> ReadLinuxProcessTable()
    {
        var result = new List<(int, int)>();
        if (!OperatingSystem.IsLinux())
            return result;
        try
        {
            foreach (string directory in Directory.EnumerateDirectories("/proc"))
            {
                string name = Path.GetFileName(directory);
                if (name.Length == 0 || !char.IsAsciiDigit(name[0]))
                    continue;
                try
                {
                    if (TryParseProcStat(File.ReadAllText(Path.Combine(directory, "stat")), out int pid, out int parent))
                        result.Add((pid, parent));
                }
                catch (IOException)
                {
                    // The process exited meanwhile.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        return result;
    }
}
