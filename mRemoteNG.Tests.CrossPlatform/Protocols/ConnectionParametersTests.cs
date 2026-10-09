using FluentAssertions;
using mRemoteNG.Protocols.Abstractions;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.Protocols;

/// <summary>
/// Tests for <see cref="ConnectionParameters"/> construction and validation.
/// </summary>
public sealed class ConnectionParametersTests
{
    [Fact]
    public void DisplayName_ShouldCombineHostnameAndPort()
    {
        var p = new ConnectionParameters
        {
            Hostname = "192.168.1.1",
            Port = 22,
            Protocol = ProtocolType.Ssh,
        };

        p.DisplayName.Should().Be("192.168.1.1:22");
    }

    [Fact]
    public void Extras_ShouldBeEmptyByDefault()
    {
        var p = new ConnectionParameters
        {
            Hostname = "host",
            Port = 80,
            Protocol = ProtocolType.Http,
        };

        p.Extras.Should().BeEmpty();
    }

    [Fact]
    public void Extras_ShouldAllowCustomValues()
    {
        var extras = new Dictionary<string, string>
        {
            ["rdp.colorDepth"] = "32",
            ["rdp.gateway"] = "gw.example.com",
        };

        var p = new ConnectionParameters
        {
            Hostname = "dc01",
            Port = 3389,
            Protocol = ProtocolType.Rdp,
            Extras = extras,
        };

        p.Extras["rdp.colorDepth"].Should().Be("32");
        p.Extras["rdp.gateway"].Should().Be("gw.example.com");
    }

    [Theory]
    [InlineData(ProtocolType.Ssh, "SSH")]
    [InlineData(ProtocolType.Rdp, "RDP")]
    [InlineData(ProtocolType.Vnc, "VNC")]
    [InlineData(ProtocolType.Http, "HTTP")]
    public void ProtocolType_EnumValues_ShouldHaveCorrectNames(ProtocolType type, string expectedName)
    {
        type.ToString().ToUpperInvariant().Should().Be(expectedName);
    }
}
