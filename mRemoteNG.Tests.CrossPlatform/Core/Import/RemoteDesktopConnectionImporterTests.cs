using FluentAssertions;
using mRemoteNG.Core.Config.Import;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Security.Factories;
using Xunit;
using static mRemoteNG.Tests.CrossPlatform.Core.Import.ImportTestHelpers;

namespace mRemoteNG.Tests.CrossPlatform.Core.Import;

/// <summary>Uses the legacy (UTF-16) .rdp fixture from mRemoteNGTests/Resources.</summary>
public class RemoteDesktopConnectionImporterTests
{
    [Fact]
    public void Import_RdpFile_AddsOneConnectionNamedAfterFile()
    {
        var root = NewRoot();

        var result = new ConnectionImportService(new CryptoProviderFactory())
            .Import(ImportSourceType.RemoteDesktopConnectionFile, Fixture("test_remotedesktopconnection.rdp"), root);

        var connection = root.Children.Should().ContainSingle().Subject;
        result.ImportedNodes.Should().ContainSingle().Which.Should().BeSameAs(connection);
        connection.Name.Should().Be("test_remotedesktopconnection");
        connection.Protocol.Should().Be(ProtocolType.RDP);
        connection.Hostname.Should().Be("testhostname.domain.com");
        connection.Port.Should().Be(9933);
        connection.Username.Should().Be("myusernamehere");
        connection.Domain.Should().Be("myspecialdomain");
        connection.Colors.Should().Be(RDPColors.Colors24Bit);
        connection.CacheBitmaps.Should().BeTrue();
        connection.Resolution.Should().Be(RDPResolutions.FitToWindow);
        connection.EnableFontSmoothing.Should().BeTrue();
        connection.EnableDesktopComposition.Should().BeTrue();
        connection.RedirectSmartCards.Should().BeTrue();
        connection.RedirectDiskDrives.Should().Be(RDPDiskDrives.Local);
        connection.RedirectPorts.Should().BeTrue();
        connection.RedirectPrinters.Should().BeTrue();
        connection.RedirectSound.Should().Be(RDPSounds.BringToThisComputer);
        connection.LoadBalanceInfo.Should().Be("tsv://MS Terminal Services Plugin.1.RDS-NAME");
        connection.RDPStartProgram.Should().Be("alternate shell");
        connection.RDGatewayHostname.Should().Be("gatewayhostname.domain.com");
        connection.RDPAuthenticationLevel.Should().Be(AuthenticationLevel.WarnOnFailedAuth);
    }

    [Fact]
    public void Import_DisableWallpaperAndThemes_HidesThem()
    {
        // "disable wallpaper:i:1" means no wallpaper; the legacy importer inverted this.
        var root = NewRoot();
        new RemoteDesktopConnectionImporter().Import(Fixture("test_remotedesktopconnection.rdp"), root);

        var connection = root.Children.Single();
        connection.DisplayWallpaper.Should().BeFalse();
        connection.DisplayThemes.Should().BeFalse();
        connection.DisableFullWindowDrag.Should().BeTrue();
        connection.DisableMenuAnimations.Should().BeTrue();
    }
}
