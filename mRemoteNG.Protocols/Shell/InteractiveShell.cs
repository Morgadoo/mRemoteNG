using System.Collections;
using System.Diagnostics;
using System.Text;
using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Protocols.Shell;

/// <summary>
/// A local interactive shell bound to a <see cref="TerminalView"/> (the bottom Terminal tab).
///
/// On Linux the shell runs on a real pseudo-terminal, so it is interactive: it prints a prompt,
/// echoes keystrokes and hears window-size changes. A shell started on redirected pipes is not
/// interactive — <c>bash --login</c> reads the profile and then waits with no prompt.
/// macOS uses <c>script(1)</c>. Windows keeps the previous console-subsystem PowerShell launch.
/// </summary>
public sealed class InteractiveShell : IDisposable
{
    private readonly TerminalView _view;
    private readonly UnixPseudoTerminal? _pty;
    private readonly Process? _process;
    private readonly Decoder? _decoder;
    private int _disposed;

    /// <summary>False once the shell process has been reaped.</summary>
    public bool IsRunning =>
        Volatile.Read(ref _disposed) == 0 && (_pty is not null ? !_pty.HasExited : _process is { HasExited: false });

    private InteractiveShell(TerminalView view, UnixPseudoTerminal? pty, Process? process)
    {
        _view = view;
        _pty = pty;
        _process = process;
        if (pty is not null)
            _decoder = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetDecoder();

        _view.DataToSend += OnDataToSend;
        _view.TerminalResized += OnTerminalResized;
    }

    /// <summary>Starts the user's shell (<c>$SHELL -il</c>, or PowerShell on Windows) on <paramref name="view"/>.</summary>
    public static InteractiveShell Start(TerminalView view)
    {
        var (program, arguments) = ResolveShell();
        return Start(view, program, arguments);
    }

    /// <summary>Starts <paramref name="program"/> on <paramref name="view"/>. Tests use this to avoid the login profile.</summary>
    internal static InteractiveShell Start(TerminalView view, string program, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(view);
        int columns = Math.Max(1, view.TerminalCols);
        int rows = Math.Max(1, view.TerminalRows);

        var home = HomeDirectory();
        if (UnixPseudoTerminal.IsSupported)
        {
            try
            {
                var pty = UnixPseudoTerminal.Start(program, arguments, EnvironmentForTerminal(), columns, rows, home);
                var shell = new InteractiveShell(view, pty, process: null);
                pty.StartReading(shell.OnPtyData, shell.OnShellExited);
                return shell;
            }
            catch (IOException)
            {
                // Fall through to script(1) or pipes.
            }
        }

        var psi = LocalShellProtocol.CreateStartInfo([program, .. arguments], columns, rows);
        psi.Environment["TERM"] = "xterm-256color";
        if (home.Length > 0)
            psi.WorkingDirectory = home;
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var piped = new InteractiveShell(view, pty: null, process);
        process.Exited += (_, _) => piped.OnShellExited(process.HasExited ? process.ExitCode : null);
        try
        {
            process.Start();
        }
        catch
        {
            piped.Dispose();
            throw;
        }
        piped.StartPipeReads();
        return piped;
    }

    private static (string Program, string[] Arguments) ResolveShell()
    {
        if (OperatingSystem.IsWindows())
            return ("powershell.exe", ["-NoLogo", "-NoProfile"]);

        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrEmpty(shell) || !File.Exists(shell))
            shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";

        var name = Path.GetFileName(shell);
        // -i makes it interactive (prompt and echo). -l reads the login profile, which is what
        // the bottom terminal used to request — without -i a login shell on a pipe exits after it.
        var arguments = name is "bash" or "zsh" or "fish" ? new[] { "-il" } : new[] { "-i" };
        return (shell, arguments);
    }

    /// <summary>The bottom terminal starts in the user's home directory, whatever directory launched the app.</summary>
    private static string HomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetEnvironmentVariable("HOME") ?? "";
        return Directory.Exists(home) ? home : "";
    }

    private static Dictionary<string, string> EnvironmentForTerminal()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environment[(string)entry.Key] = entry.Value as string ?? "";
        environment["TERM"] = "xterm-256color";
        environment["COLORTERM"] = "truecolor";
        environment.Remove("COLUMNS");
        environment.Remove("LINES");
        return environment;
    }

    private void StartPipeReads()
    {
        _ = ReadPipeAsync(_process!.StandardOutput);
        _ = ReadPipeAsync(_process.StandardError);
    }

    private async Task ReadPipeAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                int read = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                _view.Write(new string(buffer, 0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private void OnShellExited(int? exitCode)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _view.Write(exitCode is int code
            ? $"\r\nThe shell exited ({code}).\r\n"
            : "\r\nThe shell exited.\r\n");
    }

    private void OnPtyData(byte[] buffer, int count)
    {
        var chars = new char[_decoder!.GetCharCount(buffer, 0, count)];
        int decoded = _decoder.GetChars(buffer, 0, count, chars, 0);
        if (decoded > 0)
            _view.Write(new string(chars, 0, decoded));
    }

    private void OnDataToSend(object? sender, byte[] data)
    {
        if (Volatile.Read(ref _disposed) != 0 || data.Length == 0)
            return;
        try
        {
            if (_pty is not null)
            {
                if (!_pty.HasExited)
                    _pty.Write(data);
                return;
            }

            if (_process is { HasExited: false })
            {
                _process.StandardInput.BaseStream.Write(data);
                _process.StandardInput.BaseStream.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private void OnTerminalResized(object? sender, TerminalSizeEventArgs e)
    {
        if (_pty is null || Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            _pty.Resize(e.Columns, e.Rows, e.PixelWidth, e.PixelHeight);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _view.DataToSend -= OnDataToSend;
        _view.TerminalResized -= OnTerminalResized;

        if (_pty is not null)
        {
            _pty.Dispose();
            return;
        }

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
        _process?.Dispose();
    }
}
