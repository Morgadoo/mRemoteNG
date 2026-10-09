using System.Diagnostics;
using Avalonia;
using Avalonia.Platform;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Rdp;

namespace mRemoteNG.Protocols.Embedding;

/// <summary>A program's window living inside one of our native windows, plus the glue that sizes and focuses it.</summary>
internal sealed class EmbeddedForeignWindow(nint window, IEmbeddedWindowSupport? support) : IDisposable
{
    public nint Window { get; } = window;

    /// <summary>Resize/focus glue (shared with the RDP embedding); null when it could not be created.</summary>
    public IEmbeddedWindowSupport? Support { get; } = support;

    public void Dispose() => Support?.Dispose();
}

/// <summary>
/// Generic "put another program's top-level window into this native host" API on top of the RDP embedding glue
/// (<see cref="EmbeddedWindowSupport"/>): finds the window of a launched process and reparents it — X11 via
/// <see cref="X11ForeignWindows"/>, Windows via <see cref="Win32ForeignWindows"/>. macOS cannot embed foreign windows.
/// </summary>
internal static class ForeignWindowEmbedder
{
    public static bool CanEmbedInto(IPlatformHandle parent) => EmbeddedWindowSupport.CanEmbedInto(parent);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for <paramref name="process"/> to show a window and moves it into
    /// <paramref name="parent"/>. Returns null when no window appeared (the program keeps running on its own) or the
    /// window could not be moved.
    /// </summary>
    /// <param name="showHost">Called once the window was found, before it is moved: show the native host at its real size.</param>
    public static async Task<EmbeddedForeignWindow?> EmbedAsync(IPlatformHandle parent, Process process, PixelSize size,
        TimeSpan timeout, ILogger logger, Func<Task> showHost, CancellationToken ct)
    {
        if (!CanEmbedInto(parent))
            return null;

        if (OperatingSystem.IsLinux())
        {
            // The glue must watch the parent before the window arrives, to size it when it is mapped there.
            var support = EmbeddedWindowSupport.Create(parent, logger);
            try
            {
                nint window = await X11ForeignWindows.WaitForProcessWindowAsync(process.Id, () => HasExited(process), timeout, logger, ct);
                if (window == 0)
                {
                    support?.Dispose();
                    return null;
                }
                logger.LogDebug("Found window 0x{Window:x} of process {Pid}", (long)window, process.Id);
                await showHost();
                if (!await X11ForeignWindows.ReparentAsync(window, parent.Handle, size.Width, size.Height, logger, ct))
                {
                    support?.Dispose();
                    return null;
                }
                return new EmbeddedForeignWindow(window, support);
            }
            catch
            {
                support?.Dispose();
                throw;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            nint window = await Win32ForeignWindows.WaitForMainWindowAsync(process, timeout, ct);
            if (window == 0)
                return null;
            await showHost();
            if (!Win32ForeignWindows.Reparent(window, parent.Handle, size.Width, size.Height))
                return null;
            return new EmbeddedForeignWindow(window, EmbeddedWindowSupport.Create(parent, logger));
        }

        return null;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }
}
