using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;

namespace mRemoteNG.ExternalProviders;

/// <summary>
/// Looks up an EC2 instance (connection EC2InstanceId + EC2Region) with DescribeInstances and returns
/// its public IP (the legacy behaviour), private IP or DNS name. AWS credentials come from the standard
/// AWS chain, a named profile, or an access key saved in mRemoteNG (the legacy app's only option).
/// </summary>
public sealed class AwsEc2AddressProvider(Func<AppSettings> settings, ProviderSecrets secrets) : IExternalAddressProvider
{
    private const string Name = "AWS EC2";

    public ExternalAddressProvider Kind => ExternalAddressProvider.AmazonWebServices;

    public string DisplayName => Name;

    public async Task<string> ResolveAsync(ExternalAddressRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var instanceId = request.InstanceId?.Trim() ?? string.Empty;
        if (instanceId.StartsWith("AWSAPI:", StringComparison.OrdinalIgnoreCase))
            instanceId = instanceId["AWSAPI:".Length..]; // legacy internal prefix
        if (!instanceId.StartsWith("i-", StringComparison.Ordinal))
            throw new ExternalProviderException($"{Name}: \"{request.InstanceId}\" is not an EC2 instance ID (i-…).");

        var s = settings();
        var region = Region(s, request.Region);
        using var client = await CreateClientAsync(s, region, ct);

        DescribeInstancesResponse response;
        try
        {
            response = await client.DescribeInstancesAsync(new DescribeInstancesRequest { InstanceIds = [instanceId] }, ct);
        }
        catch (AmazonEC2Exception ex) when (ex.ErrorCode is "InvalidInstanceID.NotFound" or "InvalidInstanceID.Malformed")
        {
            throw new ExternalProviderException($"{Name}: instance {instanceId} was not found in {region}.", ex);
        }
        catch (AmazonServiceException ex)
        {
            throw new ExternalProviderException($"{Name}: DescribeInstances failed in {region}: {ex.Message}", ex);
        }
        catch (AmazonClientException ex)
        {
            throw new ExternalProviderException($"{Name}: {ex.Message}", ex);
        }

        var instance = (response.Reservations ?? [])
            .SelectMany(r => r.Instances ?? [])
            .FirstOrDefault(i => i.InstanceId == instanceId)
            ?? throw new ExternalProviderException($"{Name}: instance {instanceId} was not found in {region}.");

        var (address, what) = s.AwsAddressKind switch
        {
            AwsAddressKind.PrivateIp => (instance.PrivateIpAddress, "private IP address"),
            AwsAddressKind.PublicDnsName => (instance.PublicDnsName, "public DNS name"),
            AwsAddressKind.PrivateDnsName => (instance.PrivateDnsName, "private DNS name"),
            _ => (instance.PublicIpAddress, "public IP address"),
        };
        if (string.IsNullOrWhiteSpace(address))
        {
            var state = instance.State?.Name?.Value ?? "unknown";
            throw new ExternalProviderException($"{Name}: instance {instanceId} has no {what} (state: {state}).");
        }
        return address;
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var s = settings();
        var region = Region(s, null);
        using var client = await CreateClientAsync(s, region, ct);
        try
        {
            var response = await client.DescribeInstancesAsync(new DescribeInstancesRequest { MaxResults = 5 }, ct);
            var count = (response.Reservations ?? []).Sum(r => r.Instances?.Count ?? 0);
            var more = string.IsNullOrEmpty(response.NextToken) ? string.Empty : "+";
            return $"Connected to EC2 in {region}: {count}{more} instance(s) visible.";
        }
        catch (AmazonServiceException ex)
        {
            throw new ExternalProviderException($"{Name}: DescribeInstances failed in {region}: {ex.Message}", ex);
        }
        catch (AmazonClientException ex)
        {
            throw new ExternalProviderException($"{Name}: {ex.Message}", ex);
        }
    }

    private static string Region(AppSettings s, string? connectionRegion)
    {
        var region = !string.IsNullOrWhiteSpace(connectionRegion) ? connectionRegion.Trim()
            : !string.IsNullOrWhiteSpace(s.AwsDefaultRegion) ? s.AwsDefaultRegion.Trim()
            : Environment.GetEnvironmentVariable("AWS_REGION") ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        if (string.IsNullOrWhiteSpace(region))
            throw new ExternalProviderException($"{Name}: no region: set the connection's EC2Region or a default region in Options → External Providers.");
        return region.Trim();
    }

    private async Task<AmazonEC2Client> CreateClientAsync(AppSettings s, string region, CancellationToken ct)
    {
        var config = new AmazonEC2Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(region),
            Timeout = TimeSpan.FromSeconds(30),
            MaxErrorRetry = 2,
        };
        if (!string.IsNullOrWhiteSpace(s.AwsServiceUrl))
        {
            config.ServiceURL = s.AwsServiceUrl.Trim();
            config.AuthenticationRegion = region;
        }

        switch (s.AwsCredentialSource)
        {
            case AwsCredentialSource.Profile:
                var profile = s.AwsProfile?.Trim() ?? string.Empty;
                if (profile.Length == 0 || !new CredentialProfileStoreChain().TryGetAWSCredentials(profile, out var profileCredentials))
                    throw new ExternalProviderException($"{Name}: the AWS profile \"{profile}\" was not found in ~/.aws/credentials or ~/.aws/config.");
                return new AmazonEC2Client(profileCredentials, config);

            case AwsCredentialSource.AccessKey:
                var keyId = s.AwsAccessKeyId?.Trim() ?? string.Empty;
                if (keyId.Length == 0)
                    throw new ExternalProviderException($"{Name}: the access key ID is not configured (Options → External Providers).");
                var secret = await secrets.GetAsync(s.AwsSecretAccessKeyProtected, $"aws|{keyId}",
                    new ExternalProviderPromptRequest(Name, $"Secret access key for {keyId}", IsSecret: true, "Secret access key"), ct);
                return new AmazonEC2Client(new BasicAWSCredentials(keyId, secret), config);

            default:
                // Environment, shared config/credentials files, SSO, container and instance roles.
                try
                {
                    return new AmazonEC2Client(config);
                }
                catch (AmazonClientException ex)
                {
                    throw new ExternalProviderException($"{Name}: no AWS credentials found ({ex.Message}). Configure a profile or an access key.", ex);
                }
        }
    }
}
