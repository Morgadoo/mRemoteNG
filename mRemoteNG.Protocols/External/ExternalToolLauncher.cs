using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Core.Tools;

namespace mRemoteNG.Protocols.External;

/// <summary>An external tool could not be started.</summary>
public sealed class ExternalToolException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Starts <see cref="ExternalTool"/>s on every OS: substitutes the connection variables, splits the arguments
/// (<see cref="CommandLineTokenizer"/> on Linux/macOS; on Windows the command line is passed through, as the legacy
/// escaping rules expect), opens documents/URLs with the desktop's default handler and elevates when asked
/// (Windows "runas" verb, <c>pkexec</c> on Linux, an administrator prompt via <c>osascript</c> on macOS).
/// </summary>
public sealed class ExternalToolLauncher
{
    private static readonly string[] WindowsExecutableExtensions = [".exe", ".com", ".bat", ".cmd"];

    private readonly ILogger _logger;

    public ExternalToolLauncher(ILogger<ExternalToolLauncher>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Builds the start information for <paramref name="tool"/>.
    /// </summary>
    /// <param name="allowElevation">False ignores <see cref="ExternalTool.RunElevated"/> (integrated tools are never elevated).</param>
    /// <exception cref="ExternalToolException">The file name is empty or the working directory does not exist.</exception>
    public ProcessStartInfo BuildStartInfo(ExternalTool tool, ExternalToolVariables? variables, bool allowElevation = true) =>
        BuildStartInfo(tool, variables, ExternalTool.CurrentPlatform, allowElevation);

    internal ProcessStartInfo BuildStartInfo(ExternalTool tool, ExternalToolVariables? variables, ExternalToolPlatform os, bool allowElevation)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var style = os == ExternalToolPlatform.Windows ? ArgumentEscapingStyle.WindowsShell : ArgumentEscapingStyle.Posix;
        var parser = new ExternalToolArgumentParser(variables, style);

        string fileName = ExpandHome(parser.ParseArguments(tool.FileName).Trim(), os);
        if (fileName.Length == 0)
            throw new ExternalToolException($"The external tool \"{tool.DisplayName}\" has no file name.");
        if (fileName.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)
            throw new ExternalToolException($"The file name of \"{tool.DisplayName}\" contains a control character.");

        string arguments = parser.ParseArguments(tool.Arguments);
        string workingDir = ExpandHome(parser.ParseArguments(tool.WorkingDir).Trim(), os);
        if (workingDir.Length > 0 && !Directory.Exists(workingDir))
            throw new ExternalToolException($"The working directory \"{workingDir}\" of \"{tool.DisplayName}\" does not exist.");

        ProcessStartInfo psi;
        if (tool.RunElevated && allowElevation)
            psi = BuildElevated(fileName, arguments, workingDir, os);
        else
            psi = BuildNormal(fileName, arguments, os);

        if (workingDir.Length > 0 && !(tool.RunElevated && allowElevation && os != ExternalToolPlatform.Windows))
            psi.WorkingDirectory = workingDir;
        return psi;
    }

    /// <summary>Starts the tool; returns the process, or null when the desktop handled the request without one.</summary>
    /// <exception cref="ExternalToolException">The tool could not be started.</exception>
    public Process? Start(ExternalTool tool, ExternalToolVariables? variables, bool allowElevation = true)
    {
        var psi = BuildStartInfo(tool, variables, allowElevation);
        _logger.LogInformation("Starting external tool \"{Tool}\": {FileName}", tool.DisplayName, psi.FileName);
        try
        {
            return Process.Start(psi);
        }
        catch (Win32Exception ex)
        {
            throw new ExternalToolException($"Could not start \"{tool.DisplayName}\" ({psi.FileName}): {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new ExternalToolException($"Could not start \"{tool.DisplayName}\": {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Starts the tool and, when <see cref="ExternalTool.WaitForExit"/> is set, waits until it exits.
    /// Returns the exit code when it was awaited, otherwise null. Cancelling stops the wait, not the tool.
    /// </summary>
    public async Task<int?> RunAsync(ExternalTool tool, ExternalToolVariables? variables, CancellationToken ct = default)
    {
        using var process = Start(tool, variables);
        if (process is null || !tool.WaitForExit)
            return null;
        await process.WaitForExitAsync(ct);
        _logger.LogInformation("External tool \"{Tool}\" exited with code {Code}", tool.DisplayName, process.ExitCode);
        return process.ExitCode;
    }

    // ── Normal start ───────────────────────────────────────────────────────

    private static ProcessStartInfo BuildNormal(string fileName, string arguments, ExternalToolPlatform os)
    {
        if (os == ExternalToolPlatform.Windows)
        {
            // Documents and URLs go through ShellExecute; programs get the command line exactly as the legacy app built it.
            return new ProcessStartInfo(fileName)
            {
                UseShellExecute = NeedsDesktopOpen(fileName, os),
                Arguments = arguments,
            };
        }

        var args = CommandLineTokenizer.Split(arguments);
        if (NeedsDesktopOpen(fileName, os))
        {
            var open = new ProcessStartInfo(os == ExternalToolPlatform.MacOS ? "open" : "xdg-open") { UseShellExecute = false };
            open.ArgumentList.Add(fileName);
            if (os == ExternalToolPlatform.MacOS && args.Count > 0)
            {
                open.ArgumentList.Add("--args");
                foreach (string arg in args)
                    open.ArgumentList.Add(arg);
            }
            return open;
        }

        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);
        return psi;
    }

    /// <summary>
    /// True when <paramref name="fileName"/> is a URL, a document or a macOS app bundle rather than a program,
    /// so it must be opened by the desktop (ShellExecute, <c>open</c>, <c>xdg-open</c>).
    /// </summary>
    internal static bool NeedsDesktopOpen(string fileName, ExternalToolPlatform os)
    {
        if (fileName.Contains("://", StringComparison.Ordinal) || fileName.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return true;

        if (os == ExternalToolPlatform.Windows)
        {
            string extension = Path.GetExtension(fileName);
            return extension.Length > 0 && !WindowsExecutableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        if (os == ExternalToolPlatform.MacOS && fileName.TrimEnd('/').EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return true;

        // A path to an existing file without execute permission is a document.
        if (!OperatingSystem.IsWindows() && fileName.Contains('/') && File.Exists(fileName))
        {
            const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(fileName) & anyExecute) == 0;
        }
        return false;
    }

    // ── Elevated start ─────────────────────────────────────────────────────

    private static ProcessStartInfo BuildElevated(string fileName, string arguments, string workingDir, ExternalToolPlatform os)
    {
        switch (os)
        {
            case ExternalToolPlatform.Windows:
                return new ProcessStartInfo(fileName)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = arguments,
                };

            case ExternalToolPlatform.MacOS:
            {
                var command = new StringBuilder();
                if (workingDir.Length > 0)
                    command.Append("cd ").Append(ShellQuote(workingDir)).Append(" && ");
                command.Append(ShellQuote(fileName));
                foreach (string arg in CommandLineTokenizer.Split(arguments))
                    command.Append(' ').Append(ShellQuote(arg));

                var psi = new ProcessStartInfo("osascript") { UseShellExecute = false };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add($"do shell script \"{AppleScriptEscape(command.ToString())}\" with administrator privileges");
                return psi;
            }

            default:
            {
                // pkexec starts the program with a clean environment in "/": pass the display and the directory through env.
                var psi = new ProcessStartInfo("pkexec") { UseShellExecute = false };
                psi.ArgumentList.Add(FindOnPath("env") ?? "/usr/bin/env");
                if (workingDir.Length > 0)
                    psi.ArgumentList.Add($"--chdir={workingDir}");
                foreach (string name in (string[])["DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR"])
                {
                    string? value = Environment.GetEnvironmentVariable(name);
                    if (!string.IsNullOrEmpty(value))
                        psi.ArgumentList.Add($"{name}={value}");
                }
                psi.ArgumentList.Add(fileName.Contains('/') ? fileName : FindOnPath(fileName) ?? fileName);
                foreach (string arg in CommandLineTokenizer.Split(arguments))
                    psi.ArgumentList.Add(arg);
                return psi;
            }
        }
    }

    /// <summary>Quotes a word for a POSIX shell.</summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    internal static string AppleScriptEscape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string ExpandHome(string path, ExternalToolPlatform os)
    {
        if (os == ExternalToolPlatform.Windows || !(path == "~" || path.StartsWith("~/", StringComparison.Ordinal)))
            return path;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
    }

    internal static string? FindOnPath(string program)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, program);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>A readable form of the command for the log, with the arguments left out (they may hold a password).</summary>
    public static string Describe(ProcessStartInfo psi) =>
        psi.ArgumentList.Count > 0 || psi.Arguments.Length > 0 ? $"{psi.FileName} …" : psi.FileName;
}
