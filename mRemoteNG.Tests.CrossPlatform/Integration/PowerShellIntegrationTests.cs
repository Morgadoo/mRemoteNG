using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Shell;
using mRemoteNG.Protocols.Ssh;
using Xunit;
using ProtocolType = mRemoteNG.Protocols.Abstractions.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Integration;

/// <summary>
/// Runs a real local <c>pwsh</c> through <see cref="PowerShellProtocol"/> and types into it the way the
/// terminal view does (Enter is "\r"). Skipped when pwsh is not on PATH. Linux only: that is where the
/// pseudo-terminal host is implemented and verified (the pipe host is exercised there too).
/// </summary>
public sealed class PowerShellIntegrationTests
{
    private static readonly string? Pwsh = LocalShellProtocol.FindOnPath("pwsh");

    private static readonly ConnectionParameters Localhost = new()
    {
        Hostname = "localhost",
        Port = 0,
        Protocol = ProtocolType.PowerShell,
    };

    [SkippableTheory]
    [InlineData(nameof(PowerShellHostMode.PseudoTerminal))]
    [InlineData(nameof(PowerShellHostMode.Script))]
    [InlineData(nameof(PowerShellHostMode.Pipes))]
    public async Task EnterRunsCommands_AndFormattedOutputIsShown(string hostMode)
    {
        var mode = Enum.Parse<PowerShellHostMode>(hostMode);
        Skip.IfNot(OperatingSystem.IsLinux() && Pwsh is not null, "needs pwsh on PATH (Linux)");
        Skip.If(mode == PowerShellHostMode.Script && LocalShellProtocol.FindOnPath("script") is null, "needs script(1)");
        using var protocol = new PowerShellProtocol(NullLogger<PowerShellProtocol>.Instance) { ForcedHostMode = mode };
        var view = (TerminalView)protocol.CreateView();
        await protocol.ConnectAsync(Localhost);
        protocol.Mode.Should().Be(mode);
        var terminal = (ITerminalProtocol)protocol;
        await WaitForAsync(view, screen => screen.Contains("PS ", StringComparison.Ordinal), "the prompt");

        await terminal.SendInputAsync("Get-Date -Year 2001 -Month 2 -Day 3 -Format yyyy-MM-dd\r"u8.ToArray());
        await WaitForAsync(view, screen => screen.Contains("2001-02-03", StringComparison.Ordinal), "2001-02-03");

        await terminal.SendInputAsync("$PSVersionTable.PSVersion.Major\r"u8.ToArray());
        await WaitForAsync(view, screen => Lines(screen).Any(l => l == "7"), "a line reading 7");

        // Formatted (table) output used to come out as blank lines without a terminal.
        await terminal.SendInputAsync("$PSVersionTable.PSVersion\r"u8.ToArray());
        await WaitForAsync(view, screen => Lines(screen).Any(l => l.StartsWith("Major", StringComparison.Ordinal) && l.Contains("Minor", StringComparison.Ordinal)), "the PSVersion table header");

        await protocol.DisconnectAsync();
        protocol.State.Should().Be(ConnectionState.Disconnected);
    }

    [SkippableFact]
    public async Task WindowSizeChanges_ReachPwsh()
    {
        Skip.IfNot(OperatingSystem.IsLinux() && Pwsh is not null, "needs pwsh on PATH (Linux)");
        using var protocol = new PowerShellProtocol(NullLogger<PowerShellProtocol>.Instance);
        var view = (TerminalView)protocol.CreateView();
        await protocol.ConnectAsync(Localhost);
        protocol.Mode.Should().Be(PowerShellHostMode.PseudoTerminal);
        var terminal = (ITerminalProtocol)protocol;
        await WaitForAsync(view, screen => screen.Contains("PS ", StringComparison.Ordinal), "the prompt");

        // Started at the view's size.
        await terminal.SendInputAsync("\"w=$($Host.UI.RawUI.WindowSize.Width)\"\r"u8.ToArray());
        await WaitForAsync(view, screen => Lines(screen).Any(l => l == $"w={view.TerminalCols}"), $"w={view.TerminalCols}");

        // What the view's TerminalResized event triggers.
        protocol.ResizeTerminal(73, 21);
        // The pseudo-terminal has the new size as soon as ResizeTerminal returns, but pwsh learns of it through
        // SIGWINCH, which .NET handles on a background thread; until then $Host.UI.RawUI.WindowSize is the cached
        // old size. A command typed in the same instant can therefore still see 80x24: ask until the answer changes.
        var query = "\"size=$($Host.UI.RawUI.WindowSize.Width)x$($Host.UI.RawUI.WindowSize.Height)\"\r"u8.ToArray();
        await WaitForAsync(view, screen => Lines(screen).Any(l => l == "size=73x21"), "size=73x21",
            resend: () => terminal.SendInputAsync(query));

        await protocol.DisconnectAsync();
    }

    [SkippableFact]
    public async Task ExitingPwsh_EndsTheSession()
    {
        Skip.IfNot(OperatingSystem.IsLinux() && Pwsh is not null, "needs pwsh on PATH (Linux)");
        using var protocol = new PowerShellProtocol(NullLogger<PowerShellProtocol>.Instance);
        var view = (TerminalView)protocol.CreateView();
        await protocol.ConnectAsync(Localhost);
        await WaitForAsync(view, screen => screen.Contains("PS ", StringComparison.Ordinal), "the prompt");

        await ((ITerminalProtocol)protocol).SendInputAsync("exit\r"u8.ToArray());

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (protocol.State != ConnectionState.Disconnected && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        protocol.State.Should().Be(ConnectionState.Disconnected);
    }

    private static IEnumerable<string> Lines(string screen) =>
        screen.Split('\n').Select(l => l.Trim());

    /// <summary>Waits for <paramref name="condition"/>; <paramref name="resend"/>, if given, runs now and every 2 seconds.</summary>
    private static async Task WaitForAsync(TerminalView view, Func<string, bool> condition, string what, Func<Task>? resend = null)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var nextResend = DateTime.UtcNow;
        while (!condition(view.GetScreenText()))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{what} did not appear. Screen:\n{view.GetScreenText()}");
            if (resend is not null && DateTime.UtcNow >= nextResend)
            {
                await resend();
                nextResend = DateTime.UtcNow.AddSeconds(2);
            }
            await Task.Delay(50);
        }
    }
}
