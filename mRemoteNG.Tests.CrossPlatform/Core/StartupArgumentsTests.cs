using FluentAssertions;
using mRemoteNG.Core.App;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Core;

public sealed class StartupArgumentsTests
{
    [Fact]
    public void NoArguments_IsEmpty()
    {
        var args = StartupArguments.Parse([]);
        args.ConnectionFile.Should().BeNull();
        args.NoReconnect.Should().BeFalse();
        args.Portable.Should().BeFalse();
        StartupArguments.Parse(null).Should().Be(StartupArguments.Empty);
    }

    [Theory]
    [InlineData("/cons:C:\\Users\\me\\confCons.xml", "C:\\Users\\me\\confCons.xml")]
    [InlineData("/cons=team.xml", "team.xml")]
    [InlineData("-c:\"/srv/my cons.xml\"", "/srv/my cons.xml")]
    [InlineData("--cons=/srv/cons.xml", "/srv/cons.xml")]
    public void ConsSwitch_WithEnclosedValue(string arg, string expected)
    {
        StartupArguments.Parse([arg]).ConnectionFile.Should().Be(expected);
    }

    [Fact]
    public void ConsSwitch_ValueAsNextArgument()
    {
        StartupArguments.Parse(["/c", "/home/me/cons.xml", "/noreconnect"]).Should().BeEquivalentTo(new
        {
            ConnectionFile = "/home/me/cons.xml",
            NoReconnect = true,
        });
    }

    [Theory]
    [InlineData("/home/me/confCons.xml")]
    [InlineData("confCons.xml")]
    [InlineData("C:\\conf\\confCons.xml")]
    public void BareArgument_IsTheConnectionFile_EvenAUnixPathStartingWithSlash(string path)
    {
        var args = StartupArguments.Parse([path]);
        args.ConnectionFile.Should().Be(path);
        args.UnknownSwitches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/noreconnect")]
    [InlineData("/norc")]
    [InlineData("-noreconnect")]
    [InlineData("--NoReconnect")]
    public void NoReconnect(string arg)
    {
        StartupArguments.Parse([arg]).NoReconnect.Should().BeTrue();
    }

    [Fact]
    public void Reset_ImpliesPositionPanelsAndToolbar()
    {
        var args = StartupArguments.Parse(["/reset"]);
        args.ResetWindowPosition.Should().BeTrue();
        args.ResetPanels.Should().BeTrue();
        args.ResetToolbar.Should().BeTrue();
        args.ResetSettings.Should().BeFalse("/reset only resets the layout, as in the legacy app");
    }

    [Fact]
    public void IndividualResetSwitches_AndShortForms()
    {
        StartupArguments.Parse(["/rp"]).Should().BeEquivalentTo(new { ResetWindowPosition = true, ResetPanels = false, ResetToolbar = false });
        StartupArguments.Parse(["/rpnl"]).Should().BeEquivalentTo(new { ResetWindowPosition = false, ResetPanels = true, ResetToolbar = false });
        StartupArguments.Parse(["/rtbr"]).Should().BeEquivalentTo(new { ResetWindowPosition = false, ResetPanels = false, ResetToolbar = true });
        StartupArguments.Parse(["/resetsettings"]).ResetSettings.Should().BeTrue();
    }

    [Fact]
    public void PortableAndMinimized()
    {
        var args = StartupArguments.Parse(["--portable", "--minimized", "cons.xml"]);
        args.Portable.Should().BeTrue();
        args.StartMinimized.Should().BeTrue();
        args.ConnectionFile.Should().Be("cons.xml");
    }

    [Fact]
    public void DesignGallery_IsADeveloperFlag()
    {
        StartupArguments.Parse(["--design-gallery"]).DesignGallery.Should().BeTrue();
        StartupArguments.Parse(["--design-gallery"]).UnknownSwitches.Should().BeEmpty();
        StartupArguments.Parse(["cons.xml"]).DesignGallery.Should().BeFalse();
    }

    [Fact]
    public void SmokeTest_TakesTheReportDirectory_ThenTheConnectionFile()
    {
        var args = StartupArguments.Parse(["--smoke-test", "out/smoke", "ci/smoke/confCons.xml"]);

        args.Should().BeEquivalentTo(new
        {
            SmokeTest = true,
            SmokeTestReportDirectory = "out/smoke",
            ConnectionFile = "ci/smoke/confCons.xml",
        });
        args.UnknownSwitches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("--smoke-test=/tmp/report", "/tmp/report")]
    [InlineData("--smoke-test:\"C:\\smoke out\"", "C:\\smoke out")]
    public void SmokeTest_WithEnclosedValue(string arg, string expected)
    {
        var args = StartupArguments.Parse([arg]);
        args.SmokeTest.Should().BeTrue();
        args.SmokeTestReportDirectory.Should().Be(expected);
        args.ConnectionFile.Should().BeNull();
    }

    [Fact]
    public void SmokeTest_WithoutDirectory_IsFlaggedWithNoDirectory()
    {
        var args = StartupArguments.Parse(["--smoke-test"]);
        args.SmokeTest.Should().BeTrue();
        args.SmokeTestReportDirectory.Should().BeNull();
        StartupArguments.Parse(["cons.xml"]).SmokeTest.Should().BeFalse();
    }

    [Fact]
    public void SwitchValueFalse_TurnsItOff()
    {
        StartupArguments.Parse(["/noreconnect:false", "--portable=0"]).Should().BeEquivalentTo(new { NoReconnect = false, Portable = false });
    }

    [Fact]
    public void UnknownDashedSwitches_AreCollected_NotTakenAsFiles()
    {
        var args = StartupArguments.Parse(["--verbose", "-psn_0_12345", "file.xml"]);
        args.UnknownSwitches.Should().Equal("verbose", "psn_0_12345");
        args.ConnectionFile.Should().Be("file.xml");
    }

    [Fact]
    public void DoubleDash_EndsOptions()
    {
        StartupArguments.Parse(["--", "--portable"]).Should().BeEquivalentTo(new { ConnectionFile = "--portable", Portable = false });
    }

    [Fact]
    public void ResolveConnectionFile_FallsBackToTheDataDirectory_LikeTheLegacyApp()
    {
        var args = StartupArguments.Parse(["/cons:team.xml"]);
        var data = Path.Combine(Path.GetTempPath(), "mrng-data");
        var inData = Path.Combine(data, "team.xml");

        args.ResolveConnectionFile(data, p => p == inData).Should().Be(inData);
        args.ResolveConnectionFile(data, _ => false).Should().Be(Path.GetFullPath("team.xml"), "a missing file is reported by its full path");
        StartupArguments.Empty.ResolveConnectionFile(data).Should().BeNull();
    }
}
