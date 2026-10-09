using FluentAssertions;
using mRemoteNG.Core.Settings;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Settings;

/// <summary>The Tabs &amp; Panels / session options added for the session area.</summary>
public class AppSettingsSessionOptionsTests
{
    [Fact]
    public void Defaults_MatchTheLegacyApp()
    {
        var settings = new AppSettings();

        settings.DoubleClickOnTabClosesIt.Should().BeTrue();
        settings.ShowProtocolOnTabs.Should().BeFalse();
        settings.ShowLogonInfoOnTabs.Should().BeFalse();
        settings.IdentifyQuickConnectTabs.Should().BeFalse();
        settings.AlwaysShowPanelSelectionDlg.Should().BeFalse();
        settings.CreateEmptyPanelOnStartUp.Should().BeFalse();
        settings.StartUpPanelName.Should().Be("General");
        settings.OpenConnectionsFromLastSession.Should().BeFalse();
        settings.ReconnectOnDisconnect.Should().BeFalse();
        settings.ReconnectAttempts.Should().Be(5);
        settings.WindowLayout.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void ReconnectAttempts_OutOfRange_IsInvalid_AndNormalizedToTheDefault(int attempts)
    {
        var settings = new AppSettings { ReconnectAttempts = attempts };

        settings.Validate().Should().ContainSingle().Which.Should().Contain("Reconnect attempts");
        settings.Normalize().Should().Contain(nameof(AppSettings.ReconnectAttempts));
        settings.ReconnectAttempts.Should().Be(AppSettings.DefaultReconnectAttempts);
    }

    [Fact]
    public void NullStrings_AreNormalized()
    {
        var settings = new AppSettings { WindowLayout = null!, StartUpPanelName = null! };

        settings.Normalize();

        settings.WindowLayout.Should().BeEmpty();
        settings.StartUpPanelName.Should().Be("General");
    }
}
