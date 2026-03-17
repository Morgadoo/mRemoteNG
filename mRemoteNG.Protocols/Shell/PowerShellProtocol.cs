using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;

namespace mRemoteNG.Protocols.Shell;

/// <summary>
/// Cross-platform PowerShell protocol.
/// Spawns <c>pwsh</c> (PowerShell 7+) as a subprocess and presents
/// the I/O inside the shared <see cref="TerminalView"/>.
///
/// On Windows: falls back to <c>powershell.exe</c> if <c>pwsh</c> is not installed.
/// On Linux/macOS: uses <c>pwsh</c> from PATH.
///
/// When <see cref="ConnectionParameters.Hostname"/> is not "localhost" or
/// "127.0.0.1", the protocol runs:
///   <c>pwsh -NoLogo -Command "Enter-PSSession -ComputerName {host} -Credential {user}"</c>
/// </summary>
public sealed class PowerShellProtocol : ProtocolBase, IVisualProtocol
{
    private readonly ILogger<PowerShellProtocol> _logger;
    private Process? _process;
    private TerminalView? _view;
    private CancellationTokenSource? _cts;

    public PowerShellProtocol(ILogger<PowerShellProtocol> logger) => _logger = logger;

    public Control CreateView()
    {
        _view = new TerminalView();
        _view.DataToSend += OnDataToSend;
        return _view;
    }

    public override async Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        State = ConnectionState.Connecting;
        RaiseStatus("Launching PowerShell…");

        string pwshBin = FindPowerShell();
        bool isRemote = !IsLocalhost(parameters.Hostname);

        string arguments = isRemote
            ? $"-NoLogo -NoProfile -Command \"Enter-PSSession -ComputerName '{parameters.Hostname}' -Credential '{parameters.Username}'\""
            : "-NoLogo -NoProfile -Interactive";

        _process = new Process
        {
            StartInfo = new ProcessStartInfo(pwshBin, arguments)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
            EnableRaisingEvents = true,
        };

        _process.Exited += (_, _) =>
        {
            State = ConnectionState.Disconnected;
            RaiseStatus("PowerShell session ended.");
        };

        _process.Start();
        State = ConnectionState.Connected;
        RaiseStatus(isRemote
            ? $"PowerShell → {parameters.Hostname}"
            : "PowerShell (local)");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = ReadLoopAsync(_process.StandardOutput, _cts.Token);
        _ = ReadLoopAsync(_process.StandardError, _cts.Token);

        await Task.CompletedTask;
    }

    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        _cts?.Cancel();
        if (_process is { HasExited: false })
        {
            try
            {
                // Send exit command gracefully
                await _process.StandardInput.WriteLineAsync("exit");
                await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(3), ct);
            }
            catch { _process.Kill(); }
        }
        State = ConnectionState.Disconnected;
    }

    private async Task ReadLoopAsync(System.IO.StreamReader reader, CancellationToken ct)
    {
        char[] buf = new char[1024];
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
            _logger.LogWarning(ex, "PowerShell read error");
        }
    }

    private void OnDataToSend(object? sender, byte[] data)
    {
        if (_process?.StandardInput is null) return;
        try
        {
            _process.StandardInput.BaseStream.Write(data);
            _process.StandardInput.BaseStream.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PowerShell write error");
        }
    }

    private static string FindPowerShell()
    {
        // Try pwsh (PowerShell 7+) first, fall back to powershell.exe on Windows
        string[] candidates = RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? ["pwsh.exe", "powershell.exe"]
            : ["pwsh"];

        foreach (string exe in candidates)
        {
            string? path = FindOnPath(exe);
            if (path is not null) return path;
        }
        return "pwsh"; // Let OS resolve
    }

    private static string? FindOnPath(string exe)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            string full = System.IO.Path.Combine(dir, exe);
            if (System.IO.File.Exists(full)) return full;
        }
        return null;
    }

    private static bool IsLocalhost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" || string.IsNullOrWhiteSpace(host);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _process?.Dispose();
        }
    }
}

/// <summary>Provides RuntimeInformation without extra using.</summary>
file static class RuntimeInformation
{
    public static bool IsOSPlatform(System.Runtime.InteropServices.OSPlatform platform) =>
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(platform);
}
