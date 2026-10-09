namespace mRemoteNG.Core.Settings;

/// <summary>How mRemoteNG logs in to Vault/OpenBao.</summary>
public enum VaultAuthMethod
{
    /// <summary>A Vault token (the only method of the legacy WinForms app).</summary>
    Token,
    /// <summary>The userpass auth method: username + password.</summary>
    UserPass,
    /// <summary>The LDAP auth method: directory username + password.</summary>
    Ldap,
    /// <summary>The AppRole auth method: role ID + secret ID.</summary>
    AppRole,
}

/// <summary>Where the AWS EC2 address provider gets its AWS credentials.</summary>
public enum AwsCredentialSource
{
    /// <summary>The standard AWS chain: environment variables, ~/.aws profiles, SSO, instance roles…</summary>
    DefaultChain,
    /// <summary>A named profile from ~/.aws/credentials or ~/.aws/config.</summary>
    Profile,
    /// <summary>An access key ID and secret access key saved in mRemoteNG (as in the legacy app).</summary>
    AccessKey,
}

/// <summary>Which address of an EC2 instance becomes the connection's hostname.</summary>
public enum AwsAddressKind
{
    /// <summary>Public IPv4 address (the legacy app's behaviour).</summary>
    PublicIp,
    PrivateIp,
    PublicDnsName,
    PrivateDnsName,
}
