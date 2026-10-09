using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.Vnc;

/// <summary>Helpers for starting external X / VNC servers in integration tests.</summary>
internal static class ExternalProcess
{
    public static string? FindExecutable(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);

    public static bool IsListening(int port)
    {
        try
        {
            using var probe = new TcpClient();
            probe.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Starts a process whose output is collected into <paramref name="log"/>.</summary>
    public static Process Start(string path, IEnumerable<string> args, StringBuilder log, int? display = null)
    {
        var start = new ProcessStartInfo(path)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        if (display is { } d) start.Environment["DISPLAY"] = $":{d}";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = Process.Start(start)!;
        process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return process;
    }

    /// <summary>Runs an X client against <paramref name="display"/> to completion; null when the tool is missing.</summary>
    public static string? RunX(int display, string tool, params string[] args)
    {
        var path = FindExecutable(tool);
        if (path is null) return null;
        var info = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.Environment["DISPLAY"] = $":{display}";
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output;
    }

    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout, Process? watched = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (watched is { HasExited: true } || DateTime.UtcNow > deadline) return false;
            Thread.Sleep(100);
        }
        return true;
    }

    public static void Stop(Process? process, int? display = null)
    {
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        process?.Dispose();
        if (display is { } d) ClearStaleDisplay(d);
    }

    /// <summary>True when display <paramref name="display"/> is free (after removing a lock left by a dead server).</summary>
    public static bool ClaimDisplay(int display)
    {
        ClearStaleDisplay(display);
        return !File.Exists($"/tmp/.X{display}-lock");
    }

    /// <summary>A killed X server leaves its lock file and socket behind; remove them when their process is gone.</summary>
    private static void ClearStaleDisplay(int display)
    {
        var lockFile = $"/tmp/.X{display}-lock";
        try
        {
            if (!File.Exists(lockFile)) return;
            if (int.TryParse(File.ReadAllText(lockFile).Trim(), out var pid) && Directory.Exists($"/proc/{pid}")) return;
            File.Delete(lockFile);
            File.Delete($"/tmp/.X11-unix/X{display}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Paints the root window with a plaid pattern plus a smooth gradient image (for Tight's gradient/JPEG paths).</summary>
    public static void PaintTestPattern(int display, string directory)
    {
        RunX(display, "xsetroot", "-mod", "7", "5", "-fg", "#d04020", "-bg", "#2040a0");
        if (FindExecutable("convert") is not { } convert || FindExecutable("display") is null) return;

        var image = Path.Combine(directory, "pattern.png");
        // Photo-like areas (plasma, swirled gradient) next to flat colours and sharp edges.
        using (var p = Process.Start(new ProcessStartInfo(convert)
        {
            ArgumentList =
            {
                "-size", "800x600", "xc:#2040a0",
                "(", "-size", "320x240", "-seed", "42", "plasma:fractal", "-blur", "0x2", ")", "-geometry", "+0+0", "-composite",
                "(", "-size", "240x240", "gradient:#ff8000-#0040ff", "-swirl", "120", ")", "-geometry", "+40+320", "-composite",
                "-fill", "#ffffff", "-draw", "rectangle 450,50 750,150",
                "-fill", "#000000", "-draw", "rectangle 470,70 520,130",
                "-fill", "#d04020", "-draw", "circle 600,350 650,400",
                image,
            },
            UseShellExecute = false,
            RedirectStandardError = true,
        })!)
        {
            p.StandardError.ReadToEnd();
            p.WaitForExit();
        }
        if (File.Exists(image))
            RunX(display, "display", "-window", "root", image);
    }
}

/// <summary>TigerVNC <c>Xvnc</c> on display :64 (port 5964) without authentication, with a test pattern.</summary>
public sealed class TigerVncFixture : IDisposable
{
    public const int DisplayNumber = 64;
    public const int Port = 5964;
    public const int Width = 800;
    public const int Height = 600;

    private readonly Process? _xvnc;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mremoteng-tigervnc-{Guid.NewGuid():N}");
    private readonly StringBuilder _log = new();

    public TigerVncFixture()
    {
        var xvnc = ExternalProcess.FindExecutable("Xvnc");
        if (xvnc is null)
        {
            SkipReason = "Xvnc (TigerVNC) not installed";
            return;
        }
        if (ExternalProcess.IsListening(Port) || !ExternalProcess.ClaimDisplay(DisplayNumber))
        {
            SkipReason = $"Port {Port} or display :{DisplayNumber} is already in use";
            return;
        }
        Directory.CreateDirectory(_directory);
        _xvnc = ExternalProcess.Start(xvnc,
        [
            $":{DisplayNumber}", "-rfbport", $"{Port}", "-SecurityTypes", "None", "-geometry", $"{Width}x{Height}",
            "-depth", "24", "-localhost", "-nolisten", "tcp", "-AlwaysShared",
        ], _log);
        if (!ExternalProcess.WaitUntil(() => ExternalProcess.IsListening(Port), TimeSpan.FromSeconds(15), _xvnc))
        {
            lock (_log) SkipReason = $"Xvnc did not start: {_log}";
            return;
        }
        ExternalProcess.PaintTestPattern(DisplayNumber, _directory);
    }

    public string? SkipReason { get; }

    public void Dispose()
    {
        ExternalProcess.Stop(_xvnc, DisplayNumber);
        try { Directory.Delete(_directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}

/// <summary>
/// <c>Xvfb</c> on display :135 served by <c>x11vnc</c> (LibVNCServer) on port 5965 without authentication;
/// LibVNCServer also implements Zlib, CoRRE and Tight with the gradient filter, which TigerVNC does not send.
/// </summary>
public sealed class X11VncFixture : IDisposable
{
    public const int DisplayNumber = 135;
    public const int Port = 5965;

    private readonly Process? _xvfb;
    private readonly Process? _x11vnc;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mremoteng-x11vnc-{Guid.NewGuid():N}");
    private readonly StringBuilder _log = new();

    public X11VncFixture()
    {
        var xvfb = ExternalProcess.FindExecutable("Xvfb");
        var x11vnc = ExternalProcess.FindExecutable("x11vnc");
        if (xvfb is null || x11vnc is null)
        {
            SkipReason = "Xvfb / x11vnc not installed";
            return;
        }
        if (ExternalProcess.IsListening(Port) || !ExternalProcess.ClaimDisplay(DisplayNumber))
        {
            SkipReason = $"Port {Port} or display :{DisplayNumber} is already in use";
            return;
        }
        Directory.CreateDirectory(_directory);
        _xvfb = ExternalProcess.Start(xvfb, [$":{DisplayNumber}", "-screen", "0", "800x600x24", "-nolisten", "tcp"], _log);
        if (!ExternalProcess.WaitUntil(() => File.Exists($"/tmp/.X11-unix/X{DisplayNumber}"), TimeSpan.FromSeconds(10), _xvfb))
        {
            lock (_log) SkipReason = $"Xvfb did not start: {_log}";
            return;
        }
        ExternalProcess.PaintTestPattern(DisplayNumber, _directory);
        _x11vnc = ExternalProcess.Start(x11vnc,
        [
            "-display", $":{DisplayNumber}", "-rfbport", $"{Port}", "-localhost", "-nopw", "-forever", "-shared",
            "-noxdamage", "-nocursorshape", "-nocursorpos", "-cursor", "none", "-nowf", "-nowcr", "-noscr", "-q",
        ], _log);
        if (!ExternalProcess.WaitUntil(() => ExternalProcess.IsListening(Port), TimeSpan.FromSeconds(15), _x11vnc))
        {
            lock (_log) SkipReason = $"x11vnc did not start: {_log}";
        }
    }

    public string? SkipReason { get; }

    public void Dispose()
    {
        ExternalProcess.Stop(_x11vnc);
        ExternalProcess.Stop(_xvfb, DisplayNumber);
        try { Directory.Delete(_directory, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}

/// <summary>Test classes sharing the single TigerVNC server (one display / port, so never in parallel).</summary>
[Xunit.CollectionDefinition(Name)]
public sealed class TigerVncCollection : Xunit.ICollectionFixture<TigerVncFixture>
{
    public const string Name = "TigerVNC :64";
}

/// <summary>Test classes sharing the single x11vnc server.</summary>
[Xunit.CollectionDefinition(Name)]
public sealed class X11VncCollection : Xunit.ICollectionFixture<X11VncFixture>
{
    public const string Name = "x11vnc :135";
}
