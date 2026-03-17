using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Media;
using Microsoft.Extensions.Logging;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.External;

/// <summary>
/// Generic external application protocol handler.
/// Launches any configured external tool (AnyDesk, ARD, RealVNC Viewer,
/// custom scripts, etc.) with token-substituted command-line arguments.
///
/// Supported tokens in the command template:
///   {hostname}   — resolved hostname
///   {port}       — port number
///   {username}   — username
///   {password}   — password (use with caution)
///   {domain}     — Windows domain
///
/// Example configurations in <see cref="ConnectionParameters.Extras"/>:
///   "external.command"   = "anydesk {hostname}"
///   "external.command"   = "open rdp://{username}:{password}@{hostname}"
///   "external.command"   = "mstsc.exe /v:{hostname}:{port}"
/// </summary>
public sealed class ExternalAppProtocol : ProtocolBase, IVisualProtocol
{
    private readonly ILogger<ExternalAppProtocol> _logger;
    private Process? _process;

    public ExternalAppProtocol(ILogger<ExternalAppProtocol> logger) => _logger = logger;

    public Control CreateView() => new ExternalAppView();

    public override async Task ConnectAsync(ConnectionParameters parameters, CancellationToken ct = default)
    {
        State = ConnectionState.Connecting;

        if (!parameters.Extras.TryGetValue("external.command", out string? commandTemplate)
            || string.IsNullOrWhiteSpace(commandTemplate))
        {
            State = ConnectionState.Error;
            RaiseStatus("No external command configured. Set 'external.command' in connection extras.");
            return;
        }

        string command = SubstituteTokens(commandTemplate, parameters);
        _logger.LogInformation("External app: {Command}", command);
        RaiseStatus($"Launching: {command}");

        try
        {
            (string exe, string args) = SplitCommand(command);

            _process = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = true, // Allows macOS `open`, Windows `start`, etc.
                },
                EnableRaisingEvents = true,
            };

            _process.Exited += (_, _) =>
            {
                State = ConnectionState.Disconnected;
                RaiseStatus($"External app exited (code {_process.ExitCode}).");
            };

            _process.Start();
            State = ConnectionState.Connected;
            RaiseStatus($"External app running: {exe}");
        }
        catch (Exception ex)
        {
            State = ConnectionState.Error;
            RaiseStatus($"Launch failed: {ex.Message}");
            _logger.LogError(ex, "External app launch failed");
        }

        await Task.CompletedTask;
    }

    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(); }
            catch { /* already exited */ }
        }
        State = ConnectionState.Disconnected;
        await Task.CompletedTask;
    }

    private static string SubstituteTokens(string template, ConnectionParameters p)
    {
        return template
            .Replace("{hostname}", p.Hostname, StringComparison.OrdinalIgnoreCase)
            .Replace("{port}", p.Port.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{username}", p.Username ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{password}", p.Password ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{domain}", p.Domain ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static (string exe, string args) SplitCommand(string command)
    {
        command = command.Trim();

        // Quoted executable
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end > 0)
                return (command[1..end], command[(end + 1)..].Trim());
        }

        // Unquoted
        int space = command.IndexOf(' ');
        return space < 0
            ? (command, string.Empty)
            : (command[..space], command[(space + 1)..].Trim());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _process?.Dispose();
    }
}

/// <summary>
/// Placeholder view shown while the external app runs.
/// </summary>
internal sealed class ExternalAppView : UserControl
{
    public ExternalAppView()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e));
        Content = new TextBlock
        {
            Text = "External application is running.\n" +
                   "Return to mRemoteNG to manage other sessions.",
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            FontSize = 14,
        };
    }
}
