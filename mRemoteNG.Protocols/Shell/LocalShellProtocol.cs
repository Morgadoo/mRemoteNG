using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Protocols.Shell;

/// <summary>
/// Local shell session for the legacy "Terminal" and "WSL" protocols, shown in the shared
/// <see cref="TerminalView"/>.
///
///   • Terminal, blank/localhost hostname → the user's interactive shell ($SHELL, bash or sh; cmd.exe on Windows)
///   • Terminal, other hostname           → <c>ssh [-p port] [user@]host</c> (as the legacy Terminal protocol did)
///   • WSL                                → <c>wsl.exe [-d distro] [-u user]</c>; the hostname names the distribution.
///                                          Only available on Windows — elsewhere the session reports an error.
///
/// On Linux and macOS the command runs under <c>script(1)</c> so it gets a pseudo-terminal: shells
/// show a prompt and echo input, and ssh can ask for a password. Without <c>script</c> (and on Windows)
/// the process runs on plain pipes, which works for line-oriented programs only.
/// </summary>
public sealed class LocalShellProtocol : ProtocolBase, IVisualProtocol
{
    private readonly ILogger<LocalShellProtocol> _logger;
    private Process? _process;
    private TerminalView? _view;
    private CancellationTokenSource? _cts;

    public LocalShellProtocol(ILogger<LocalShellProtocol> logger) => _logger = logger;

    public Control CreateView()
    {
        _view = new TerminalView();
        _view.DataToSend += OnDataToSend;
        return _view;
    }

    public override Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        State = ConnectionState.Connecting;

        var isWsl = parameters.Extras.TryGetValue(ConnectionParametersFactory.Keys.LocalShellMode, out var mode)
                    && string.Equals(mode, "wsl", StringComparison.OrdinalIgnoreCase);

        string[] command;
        try
        {
            command = isWsl ? BuildWslCommand(parameters) : BuildTerminalCommand(parameters);
        }
        catch (PlatformNotSupportedException ex)
        {
            State = ConnectionState.Error;
            RaiseStatus(ex.Message);
            _view?.Write(ex.Message + "\r\n");
            return Task.CompletedTask;
        }

        var psi = CreateStartInfo(command, _view?.TerminalCols ?? 80, _view?.TerminalRows ?? 24);
        RaiseStatus($"Starting {string.Join(' ', command)}");

        try
        {
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += (_, _) =>
            {
                State = ConnectionState.Disconnected;
                RaiseStatus("Shell exited.");
            };
            _process.Start();
        }
        catch (Exception ex)
        {
            State = ConnectionState.Error;
            RaiseStatus($"Could not start {command[0]}: {ex.Message}");
            _logger.LogError(ex, "Local shell start failed");
            throw;
        }

        State = ConnectionState.Connected;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ReadLoopAsync(_process.StandardOutput, _cts.Token);
        _ = ReadLoopAsync(_process.StandardError, _cts.Token);
        return Task.CompletedTask;
    }

    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        if (IsRunning())
        {
            try
            {
                _process!.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Local shell kill failed");
            }
        }
        State = ConnectionState.Disconnected;
    }

    /// <summary>Command line (program + arguments) for the Terminal protocol.</summary>
    internal static string[] BuildTerminalCommand(ConnectionParameters parameters)
    {
        var host = parameters.Hostname?.Trim() ?? "";
        if (host.Length > 0 && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            var ssh = new List<string> { "ssh" };
            if (parameters.Port > 0 && parameters.Port != 22)
                ssh.AddRange(["-p", parameters.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            ssh.Add(string.IsNullOrEmpty(parameters.Username) ? host : $"{parameters.Username}@{host}");
            return [.. ssh];
        }

        if (OperatingSystem.IsWindows())
            return [Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe"];

        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrEmpty(shell) || !File.Exists(shell))
            shell = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
        return [shell, "-i"];
    }

    /// <summary>Command line for the WSL protocol.</summary>
    /// <exception cref="PlatformNotSupportedException">Not running on Windows.</exception>
    internal static string[] BuildWslCommand(ConnectionParameters parameters)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WSL (Windows Subsystem for Linux) is only available on Windows.");

        var command = new List<string> { "wsl.exe" };
        var distro = parameters.Hostname?.Trim() ?? "";
        if (distro.Length > 0 && !distro.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            command.AddRange(["-d", distro]);
        if (!string.IsNullOrEmpty(parameters.Username))
            command.AddRange(["-u", parameters.Username]);
        return [.. command];
    }

    private static ProcessStartInfo CreateStartInfo(string[] command, int cols, int rows)
    {
        ProcessStartInfo psi;
        var script = OperatingSystem.IsWindows() ? null : FindOnPath("script");
        if (script is not null)
        {
            // Give the command a pseudo-terminal sized like the view.
            var inner = $"stty cols {cols} rows {rows} 2>/dev/null; exec {string.Join(' ', command.Select(ShellQuote))}";
            psi = new ProcessStartInfo(script);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                foreach (var arg in new[] { "-q", "/dev/null", "/bin/sh", "-c", inner })
                    psi.ArgumentList.Add(arg);
            }
            else
            {
                foreach (var arg in new[] { "-qfec", inner, "/dev/null" })
                    psi.ArgumentList.Add(arg);
            }
        }
        else
        {
            psi = new ProcessStartInfo(command[0]);
            foreach (var arg in command.Skip(1))
                psi.ArgumentList.Add(arg);
        }

        psi.UseShellExecute = false;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.Environment["TERM"] = "xterm";
        return psi;
    }

    internal static string ShellQuote(string value) =>
        value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || "-_./@:=+,".Contains(c))
            ? value
            : "'" + value.Replace("'", "'\\''") + "'";

    private static string? FindOnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var full = Path.Combine(dir, exe);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        var buf = new char[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await reader.ReadAsync(buf, ct);
                if (read == 0) break;
                _view?.Write(new string(buf, 0, read));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local shell read error");
        }
    }

    private bool IsRunning()
    {
        try
        {
            return _process is { HasExited: false };
        }
        catch (InvalidOperationException)
        {
            return false; // never started
        }
    }

    private void OnDataToSend(object? sender, byte[] data)
    {
        if (!IsRunning()) return;
        try
        {
            _process!.StandardInput.BaseStream.Write(data);
            _process.StandardInput.BaseStream.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local shell write error");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            if (IsRunning())
            {
                try { _process!.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            _process?.Dispose();
        }
    }
}
