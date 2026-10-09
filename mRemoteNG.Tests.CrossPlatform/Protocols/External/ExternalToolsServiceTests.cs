using FluentAssertions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tools;
using mRemoteNG.Protocols.External;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;

namespace mRemoteNG.Tests.CrossPlatform.Protocols.External;

public sealed class ExternalToolsServiceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mrng-tools-").FullName;
    private readonly List<ExternalToolMessageEventArgs> _messages = [];

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string FilePath => Path.Combine(_directory, ExternalToolsRepository.FileName);

    private ExternalToolsService CreateService()
    {
        var service = new ExternalToolsService(new ExternalToolsRepository(FilePath), new ExternalToolLauncher());
        service.Message += (_, e) => _messages.Add(e);
        return service;
    }

    [Fact]
    public void Tools_WithoutAFile_AreTheDefaultsForThisOs()
    {
        var service = CreateService();

        service.Tools.Select(t => t.DisplayName).Should().Equal(ExternalToolDefaults.Create().Select(t => t.DisplayName));
        File.Exists(FilePath).Should().BeFalse("defaults are written on the first save only");
    }

    [Fact]
    public void ReplaceAll_SavesAndRaisesToolsChanged()
    {
        var service = CreateService();
        int changes = 0;
        service.ToolsChanged += (_, _) => changes++;

        service.ReplaceAll([new ExternalTool("Mine", "mine")]).Should().BeTrue();

        changes.Should().Be(1);
        CreateService().Tools.Should().ContainSingle().Which.DisplayName.Should().Be("Mine");
    }

    [Fact]
    public void DamagedFile_IsReportedAndLeavesTheListEmpty()
    {
        File.WriteAllText(FilePath, "<Apps><App");
        var service = CreateService();

        service.Tools.Should().BeEmpty();
        _messages.Should().ContainSingle(m => m.Level == ExternalToolMessageLevel.Error);
    }

    [Fact]
    public void Find_PrefersAnExactMatch_ThenIgnoresCase()
    {
        var service = CreateService();
        service.ReplaceAll([new ExternalTool("ping", "a"), new ExternalTool("Ping", "b"), new ExternalTool("SSH", "c")]);

        service.Find("Ping")!.FileName.Should().Be("b");
        service.Find("ssh")!.FileName.Should().Be("c");
        service.Find("nope").Should().BeNull();
        service.Find(null).Should().BeNull();
    }

    [Fact]
    public void ToolbarTools_AreThoseMarkedForTheToolbarOnThisOs()
    {
        var other = ExternalTool.CurrentPlatform == ExternalToolPlatform.Windows ? ExternalToolPlatform.Linux : ExternalToolPlatform.Windows;
        var service = CreateService();
        service.ReplaceAll(
        [
            new ExternalTool("shown", "a"),
            new ExternalTool("hidden", "b") { ShowOnToolbar = false },
            new ExternalTool("other os", "c") { Platform = other },
        ]);

        service.ToolbarTools.Select(t => t.DisplayName).Should().Equal("shown");
    }

    [Fact]
    public async Task RunAsync_ToolForAnotherOs_IsNotStarted()
    {
        var other = ExternalTool.CurrentPlatform == ExternalToolPlatform.Windows ? ExternalToolPlatform.Linux : ExternalToolPlatform.Windows;
        var service = CreateService();

        bool started = await service.RunAsync(new ExternalTool("x", "x") { Platform = other }, null);

        started.Should().BeFalse();
        _messages.Should().ContainSingle(m => m.Level == ExternalToolMessageLevel.Warning);
    }

    [Fact]
    public async Task RunAsync_UnknownToolName_IsReported()
    {
        var service = CreateService();

        (await service.RunAsync("does not exist", null)).Should().BeFalse();
        _messages.Should().ContainSingle(m => m.Message.Contains("does not exist"));
    }

    [Fact]
    public async Task RunAsync_LaunchFailure_IsReportedNotThrown()
    {
        var service = CreateService();

        bool started = await service.RunAsync(new ExternalTool("broken", "mrng-no-such-program-" + Guid.NewGuid().ToString("N")), null);

        started.Should().BeFalse();
        _messages.Should().Contain(m => m.Level == ExternalToolMessageLevel.Error && m.Message.Contains("broken"));
    }

    [SkippableFact]
    public async Task RunAsync_Folder_RunsTheToolForEveryConnectionInside()
    {
        Skip.If(OperatingSystem.IsWindows());
        string log = Path.Combine(_directory, "log");
        var tool = new ExternalTool("log", "sh", $"-c 'echo \"$1\" >> \"$2\"' sh %HOSTNAME% \"{log}\"") { WaitForExit = true };
        var folder = new ContainerInfo();
        folder.AddChild(new ConnectionInfo { Hostname = "one" });
        var sub = new ContainerInfo();
        sub.AddChild(new ConnectionInfo { Hostname = "two" });
        folder.AddChild(sub);

        (await CreateService().RunAsync(tool, folder)).Should().BeTrue();

        File.ReadAllLines(log).Should().Equal("one", "two");
    }

    [Fact]
    public async Task RunAsync_EmptyFolder_IsReported()
    {
        var service = CreateService();

        (await service.RunAsync(new ExternalTool("t", "true"), new ContainerInfo { Name = "Empty" })).Should().BeFalse();

        _messages.Should().ContainSingle(m => m.Level == ExternalToolMessageLevel.Warning && m.Message.Contains("Empty"));
    }

    [SkippableFact]
    public async Task RunAsync_UsesTheVariablesFilter()
    {
        Skip.If(OperatingSystem.IsWindows());
        string output = Path.Combine(_directory, "user");
        var service = CreateService();
        service.VariablesFilter = v => v.Username.Length == 0 ? v with { Username = "fallback" } : v;
        var tool = new ExternalTool("u", "sh", $"-c 'echo \"$1\" > \"$2\"' sh \"%USERNAME%\" \"{output}\"") { WaitForExit = true };

        await service.RunAsync(tool, new ConnectionInfo());

        File.ReadAllText(output).Trim().Should().Be("fallback");
    }

    [Fact]
    public async Task RunAsync_IntegratedTool_OpensASessionForACopyOfTheConnection()
    {
        var service = CreateService();
        ConnectionInfo? opened = null;
        service.OpenIntegratedSession = c =>
        {
            opened = c;
            return Task.CompletedTask;
        };
        var connection = new ConnectionInfo
        {
            Name = "web01",
            Hostname = "10.0.0.5",
            Protocol = CoreProtocol.SSH2,
            PreExtApp = "before",
            PostExtApp = "after",
        };

        (await service.RunAsync(new ExternalTool("Terminal", "xterm") { TryIntegrate = true }, connection)).Should().BeTrue();

        opened.Should().NotBeNull().And.NotBeSameAs(connection);
        opened!.Protocol.Should().Be(CoreProtocol.IntApp);
        opened.ExtApp.Should().Be("Terminal");
        opened.Name.Should().Be("Terminal");
        opened.Hostname.Should().Be("10.0.0.5");
        opened.Panel.Should().Be(ExternalToolsService.ToolsPanel);
        opened.PreExtApp.Should().BeEmpty("the tab must not run the connection's before/after tools again");
        opened.PostExtApp.Should().BeEmpty();
        connection.Protocol.Should().Be(CoreProtocol.SSH2, "the original connection is unchanged");
    }
}
