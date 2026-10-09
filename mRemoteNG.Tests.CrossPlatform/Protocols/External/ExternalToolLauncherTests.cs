using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Tools;
using mRemoteNG.Protocols.External;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.External;

public sealed class ExternalToolLauncherTests : IDisposable
{
    private readonly ExternalToolLauncher _launcher = new();
    private readonly string _directory = Directory.CreateTempSubdirectory("mrng-launcher-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static ExternalToolVariables Vars(string hostname = "srv01", string password = "p w\"d") =>
        ExternalToolVariables.FromConnection(new ConnectionInfo { Hostname = hostname, Password = password, Port = 22, Username = "bob" });

    [Fact]
    public void Posix_SplitsArgumentsWithoutAShell()
    {
        var tool = new ExternalTool("t", "ssh", "-p %PORT% \"%USERNAME%\"@%HOSTNAME% --pw \"%PASSWORD%\" ; echo");

        var psi = _launcher.BuildStartInfo(tool, Vars(), ExternalToolPlatform.Linux, allowElevation: true);

        psi.FileName.Should().Be("ssh");
        psi.UseShellExecute.Should().BeFalse();
        psi.ArgumentList.Should().Equal("-p", "22", "bob@srv01", "--pw", "p w\"d", ";", "echo");
    }

    [Fact]
    public void Windows_PassesTheLegacyCommandLineThrough()
    {
        var tool = new ExternalTool("t", "cmd", "/K ping -t %HOSTNAME%");

        var psi = _launcher.BuildStartInfo(tool, Vars("a&b"), ExternalToolPlatform.Windows, allowElevation: true);

        psi.FileName.Should().Be("cmd");
        psi.UseShellExecute.Should().BeFalse();
        psi.Arguments.Should().Be("/K ping -t a^&b");
        psi.ArgumentList.Should().BeEmpty();
    }

    [Fact]
    public void Windows_Elevated_UsesRunAs()
    {
        var tool = new ExternalTool("t", "regedit.exe", "/s x", runElevated: true);

        var psi = _launcher.BuildStartInfo(tool, null, ExternalToolPlatform.Windows, allowElevation: true);

        psi.UseShellExecute.Should().BeTrue();
        psi.Verb.Should().Be("runas");
        psi.Arguments.Should().Be("/s x");
    }

    [Fact]
    public void Linux_Elevated_UsesPkexecWithTheDisplay()
    {
        var tool = new ExternalTool("t", "/usr/sbin/iftop", "-i \"%HOSTNAME%\"", _directory, runElevated: true);

        var psi = _launcher.BuildStartInfo(tool, Vars("eth0"), ExternalToolPlatform.Linux, allowElevation: true);

        psi.FileName.Should().Be("pkexec");
        psi.ArgumentList[0].Should().EndWith("env");
        psi.ArgumentList[1].Should().Be($"--chdir={_directory}");
        psi.ArgumentList.TakeLast(3).Should().Equal("/usr/sbin/iftop", "-i", "eth0");
        psi.WorkingDirectory.Should().BeEmpty("pkexec ignores it; env --chdir sets it");
    }

    [Fact]
    public void Mac_Elevated_UsesAnAdministratorPrompt()
    {
        var tool = new ExternalTool("t", "/sbin/ping", "-c 1 \"%HOSTNAME%\"", runElevated: true);

        var psi = _launcher.BuildStartInfo(tool, Vars("a\"b'c"), ExternalToolPlatform.MacOS, allowElevation: true);

        psi.FileName.Should().Be("osascript");
        psi.ArgumentList.Should().Equal("-e",
            "do shell script \"'/sbin/ping' '-c' '1' 'a\\\"b'\\\\''c'\" with administrator privileges");
    }

    [Fact]
    public void Elevation_CanBeSuppressed()
    {
        var tool = new ExternalTool("t", "xterm", "", runElevated: true);

        _launcher.BuildStartInfo(tool, null, ExternalToolPlatform.Linux, allowElevation: false).FileName.Should().Be("xterm");
    }

    [Theory]
    [InlineData("https://example.org/%HOSTNAME%", ExternalToolPlatform.Linux, true)]
    [InlineData("ping", ExternalToolPlatform.Linux, false)]
    [InlineData("/Applications/Utilities/Terminal.app", ExternalToolPlatform.MacOS, true)]
    [InlineData(@"C:\docs\readme.txt", ExternalToolPlatform.Windows, true)]
    [InlineData(@"C:\tools\putty.exe", ExternalToolPlatform.Windows, false)]
    [InlineData("cmd", ExternalToolPlatform.Windows, false)]
    public void NeedsDesktopOpen(string fileName, ExternalToolPlatform os, bool expected) =>
        ExternalToolLauncher.NeedsDesktopOpen(fileName, os).Should().Be(expected);

    [SkippableFact]
    public void Posix_NonExecutableFile_IsOpenedWithTheDesktop()
    {
        Skip.If(OperatingSystem.IsWindows());
        string document = Path.Combine(_directory, "notes.txt");
        File.WriteAllText(document, "x");

        var psi = _launcher.BuildStartInfo(new ExternalTool("t", document), null, ExternalToolPlatform.Linux, allowElevation: true);

        psi.FileName.Should().Be("xdg-open");
        psi.ArgumentList.Should().Equal(document);
    }

    [Fact]
    public void MissingWorkingDirectory_Throws()
    {
        var act = () => _launcher.BuildStartInfo(new ExternalTool("t", "ping", "", Path.Combine(_directory, "nope")), null);

        act.Should().Throw<ExternalToolException>().WithMessage("*working directory*");
    }

    [Fact]
    public void EmptyFileName_Throws()
    {
        var act = () => _launcher.BuildStartInfo(new ExternalTool("t", "  "), null);

        act.Should().Throw<ExternalToolException>().WithMessage("*no file name*");
    }

    [SkippableFact]
    public void HomeDirectory_IsExpanded()
    {
        Skip.If(OperatingSystem.IsWindows());

        var psi = _launcher.BuildStartInfo(new ExternalTool("t", "~/bin/tool"), null);

        psi.FileName.Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/bin/tool");
    }

    [SkippableFact]
    public async Task RunAsync_WaitForExit_ReturnsTheExitCode()
    {
        Skip.If(OperatingSystem.IsWindows());
        string marker = Path.Combine(_directory, "done");
        var tool = new ExternalTool("t", "sh", $"-c 'sleep 0.2; echo \"$1\" > \"$2\"; exit 3' sh %HOSTNAME% \"{marker}\"") { WaitForExit = true };

        int? code = await _launcher.RunAsync(tool, Vars("hello"));

        code.Should().Be(3);
        File.ReadAllText(marker).Trim().Should().Be("hello");
    }

    [SkippableFact]
    public async Task RunAsync_WithoutWaitForExit_ReturnsImmediately()
    {
        Skip.If(OperatingSystem.IsWindows());
        var tool = new ExternalTool("t", "sleep", "5");

        var started = DateTime.UtcNow;
        int? code = await _launcher.RunAsync(tool, null);

        code.Should().BeNull();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Start_UnknownProgram_ThrowsExternalToolException()
    {
        var act = () => _launcher.Start(new ExternalTool("t", "mrng-no-such-program-" + Guid.NewGuid().ToString("N")), null);

        act.Should().Throw<ExternalToolException>().WithMessage("*Could not start*");
    }
}
