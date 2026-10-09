using System.ComponentModel;
using FluentAssertions;
using mRemoteNG.Core.Tools;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core.Tools;

/// <summary>Mirrors mRemoteNGTests/Tools/ExternalToolTests (WaitForExit/TryIntegrate interplay) and the new fields.</summary>
public sealed class ExternalToolTests
{
    [Fact]
    public void SettingTryIntegrate_TurnsWaitForExitOff()
    {
        var tool = new ExternalTool { WaitForExit = true };

        tool.TryIntegrate = true;

        tool.WaitForExit.Should().BeFalse();
    }

    [Fact]
    public void WaitForExit_CannotBeSetWhileTryIntegrateIsOn()
    {
        var tool = new ExternalTool { TryIntegrate = true };

        tool.WaitForExit = true;

        tool.WaitForExit.Should().BeFalse();
    }

    [Fact]
    public void PropertyChanged_IsRaisedForChangedValuesOnly()
    {
        var tool = new ExternalTool("a");
        var changed = new List<string?>();
        tool.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        tool.DisplayName = "a";
        tool.DisplayName = "b";
        tool.Platform = ExternalToolPlatform.Linux;

        changed.Should().Equal(nameof(ExternalTool.DisplayName), nameof(ExternalTool.Platform));
    }

    [Fact]
    public void Clone_And_CopyFrom_CopyEverySetting()
    {
        var tool = new ExternalTool("n", "f", "a", "w", runElevated: true)
        {
            ShowOnToolbar = false,
            WaitForExit = true,
            Platform = ExternalToolPlatform.MacOS,
            IconPath = "/i.png",
        };
        var integrated = new ExternalTool { TryIntegrate = true };

        var clone = tool.Clone();
        integrated.CopyFrom(tool);

        clone.Should().BeEquivalentTo(tool, o => o.Excluding(t => t.IsAvailableOnCurrentPlatform));
        integrated.Should().BeEquivalentTo(tool, o => o.Excluding(t => t.IsAvailableOnCurrentPlatform));
        clone.Should().NotBeSameAs(tool);
    }

    [Theory]
    [InlineData(ExternalToolPlatform.Any, ExternalToolPlatform.Linux, true)]
    [InlineData(ExternalToolPlatform.Windows, ExternalToolPlatform.Windows, true)]
    [InlineData(ExternalToolPlatform.Windows, ExternalToolPlatform.Linux, false)]
    [InlineData(ExternalToolPlatform.MacOS, ExternalToolPlatform.Linux, false)]
    public void IsAvailableOn(ExternalToolPlatform toolPlatform, ExternalToolPlatform os, bool expected) =>
        new ExternalTool { Platform = toolPlatform }.IsAvailableOn(os).Should().Be(expected);

    [Fact]
    public void Defaults_MatchLegacy()
    {
        var tool = new ExternalTool();

        tool.ShowOnToolbar.Should().BeTrue();
        tool.WaitForExit.Should().BeFalse();
        tool.TryIntegrate.Should().BeFalse();
        tool.RunElevated.Should().BeFalse();
        tool.Platform.Should().Be(ExternalToolPlatform.Any);
        ((INotifyPropertyChanged)tool).Should().NotBeNull();
    }
}
