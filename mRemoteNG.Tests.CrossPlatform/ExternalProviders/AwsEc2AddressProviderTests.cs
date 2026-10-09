using System.Net;
using FluentAssertions;
using mRemoteNG.Core.Settings;
using mRemoteNG.ExternalProviders;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.ExternalProviders;

/// <summary>
/// The real AWS SDK client talks to a fake EC2 endpoint (ServiceURL override) that answers with the
/// documented DescribeInstances XML.
/// </summary>
public sealed class AwsEc2AddressProviderTests : IDisposable
{
    private const string InstanceId = "i-1234567890abcdef0";

    private static string DescribeInstancesXml(string state = "running", int code = 16, bool withPublic = true) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <DescribeInstancesResponse xmlns="http://ec2.amazonaws.com/doc/2016-11-15/">
            <requestId>8f7724cf-496f-496e-8fe3-example</requestId>
            <reservationSet>
                <item>
                    <reservationId>r-1234567890abcdef0</reservationId>
                    <ownerId>123456789012</ownerId>
                    <groupSet/>
                    <instancesSet>
                        <item>
                            <instanceId>{InstanceId}</instanceId>
                            <imageId>ami-bff32ccc</imageId>
                            <instanceState><code>{code}</code><name>{state}</name></instanceState>
                            <privateDnsName>ip-192-168-1-88.eu-west-1.compute.internal</privateDnsName>
                            <dnsName>{(withPublic ? "ec2-54-194-252-215.eu-west-1.compute.amazonaws.com" : "")}</dnsName>
                            <reason/>
                            <keyName>my_keypair</keyName>
                            <amiLaunchIndex>0</amiLaunchIndex>
                            <productCodes/>
                            <instanceType>t3.micro</instanceType>
                            <launchTime>2026-05-08T16:46:19.000Z</launchTime>
                            <placement><availabilityZone>eu-west-1c</availabilityZone><groupName/><tenancy>default</tenancy></placement>
                            <monitoring><state>disabled</state></monitoring>
                            <subnetId>subnet-56f5f633</subnetId>
                            <vpcId>vpc-11112222</vpcId>
                            <privateIpAddress>192.168.1.88</privateIpAddress>
                            {(withPublic ? "<ipAddress>54.194.252.215</ipAddress>" : "")}
                            <sourceDestCheck>true</sourceDestCheck>
                            <groupSet><item><groupId>sg-e4076980</groupId><groupName>SecurityGroup1</groupName></item></groupSet>
                            <architecture>x86_64</architecture>
                            <rootDeviceType>ebs</rootDeviceType>
                            <rootDeviceName>/dev/xvda</rootDeviceName>
                            <virtualizationType>hvm</virtualizationType>
                            <tagSet><item><key>Name</key><value>Server_1</value></item></tagSet>
                            <hypervisor>xen</hypervisor>
                            <ebsOptimized>false</ebsOptimized>
                        </item>
                    </instancesSet>
                </item>
            </reservationSet>
        </DescribeInstancesResponse>
        """;

    private const string NotFoundXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Response><Errors><Error><Code>InvalidInstanceID.NotFound</Code><Message>The instance ID 'i-0000000000000dead' does not exist</Message></Error></Errors><RequestID>ea966190-f9aa-478e-9ede-example</RequestID></Response>
        """;

    private Func<RecordedRequest, (int, string, string)> _respond = _ => (200, "text/xml", DescribeInstancesXml());
    private readonly LoopbackHttpServer _ec2;
    private readonly AppSettings _settings;

    public AwsEc2AddressProviderTests()
    {
        _ec2 = new LoopbackHttpServer(r => _respond(r));
        _settings = new AppSettings
        {
            AwsCredentialSource = AwsCredentialSource.AccessKey,
            AwsAccessKeyId = "AKIAIOSFODNN7EXAMPLE",
            AwsSecretAccessKeyProtected = TestSecrets.Protect("wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"),
            AwsServiceUrl = _ec2.Url,
        };
    }

    public void Dispose() => _ec2.Dispose();

    private AwsEc2AddressProvider Create(IExternalProviderPrompt? prompt = null) => new(() => _settings, TestSecrets.Create(prompt));

    [Fact]
    public async Task ResolveAsync_ReturnsThePublicIp_AndSignsTheRequestForTheConnectionsRegion()
    {
        var address = await Create().ResolveAsync(new ExternalAddressRequest(InstanceId, "eu-west-1"));

        address.Should().Be("54.194.252.215");
        var request = _ec2.Requests.Single();
        request.Method.Should().Be(HttpMethod.Post);
        request.Body.Should().Contain("Action=DescribeInstances").And.Contain($"InstanceId.1={InstanceId}");
        request.Header("Authorization").Should().Contain("Credential=AKIAIOSFODNN7EXAMPLE/").And.Contain("/eu-west-1/ec2/aws4_request");
    }

    [Theory]
    [InlineData(AwsAddressKind.PrivateIp, "192.168.1.88")]
    [InlineData(AwsAddressKind.PublicDnsName, "ec2-54-194-252-215.eu-west-1.compute.amazonaws.com")]
    [InlineData(AwsAddressKind.PrivateDnsName, "ip-192-168-1-88.eu-west-1.compute.internal")]
    public async Task ResolveAsync_ReturnsTheConfiguredAddress(AwsAddressKind kind, string expected)
    {
        _settings.AwsAddressKind = kind;

        var address = await Create().ResolveAsync(new ExternalAddressRequest(InstanceId, "eu-west-1"));

        address.Should().Be(expected);
    }

    [Fact]
    public async Task ResolveAsync_AcceptsTheLegacyPrefix_AndUsesTheDefaultRegion()
    {
        _settings.AwsDefaultRegion = "eu-central-1";

        await Create().ResolveAsync(new ExternalAddressRequest("AWSAPI:" + InstanceId, ""));

        _ec2.Requests.Single().Header("Authorization").Should().Contain("/eu-central-1/ec2/aws4_request");
    }

    [Fact]
    public async Task ResolveAsync_StoppedInstanceWithoutPublicIp_IsReportedWithItsState()
    {
        _respond = _ => (200, "text/xml", DescribeInstancesXml("stopped", 80, withPublic: false));

        var act = () => Create().ResolveAsync(new ExternalAddressRequest(InstanceId, "eu-west-1"));

        (await act.Should().ThrowAsync<ExternalProviderException>())
            .WithMessage($"AWS EC2: instance {InstanceId} has no public IP address (state: stopped).");
    }

    [Fact]
    public async Task ResolveAsync_UnknownInstance_IsReported()
    {
        _respond = _ => (400, "text/xml", NotFoundXml);

        var act = () => Create().ResolveAsync(new ExternalAddressRequest("i-0000000000000dead", "eu-west-1"));

        (await act.Should().ThrowAsync<ExternalProviderException>())
            .WithMessage("AWS EC2: instance i-0000000000000dead was not found in eu-west-1.");
    }

    [Fact]
    public async Task ResolveAsync_AsksForAnUnsavedSecretKey()
    {
        _settings.AwsSecretAccessKeyProtected = string.Empty;
        var prompt = new ScriptedPrompt("typed-secret");

        await Create(prompt).ResolveAsync(new ExternalAddressRequest(InstanceId, "eu-west-1"));

        prompt.Asked.Single().Message.Should().Be("Secret access key for AKIAIOSFODNN7EXAMPLE");
    }

    [Theory]
    [InlineData("web01")]
    [InlineData("")]
    public async Task ResolveAsync_RejectsSomethingThatIsNotAnInstanceId(string id)
    {
        var act = () => Create().ResolveAsync(new ExternalAddressRequest(id, "eu-west-1"));

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("*is not an EC2 instance ID*");
        _ec2.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_UnknownProfile_IsReported()
    {
        _settings.AwsCredentialSource = AwsCredentialSource.Profile;
        _settings.AwsProfile = "mremoteng-test-profile-" + Guid.NewGuid().ToString("N");

        var act = () => Create().ResolveAsync(new ExternalAddressRequest(InstanceId, "eu-west-1"));

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("AWS EC2: the AWS profile \"mremoteng-test-profile-*\" was not found*");
    }

    [Fact]
    public async Task TestAsync_CountsTheVisibleInstances()
    {
        _settings.AwsDefaultRegion = "eu-west-1";

        var message = await Create().TestAsync();

        message.Should().Be("Connected to EC2 in eu-west-1: 1 instance(s) visible.");
        _ec2.Requests.Single().Body.Should().Contain("MaxResults=5");
    }
}
