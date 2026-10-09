using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol.RDP;
using mRemoteNG.Core.Settings;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.ExternalProviders;

/// <summary>
/// Order 100: replaces the hostname with the address an external address provider returns
/// (AWS EC2: the instance's current IP / DNS name) when the connection's ExternalAddressProvider is set.
/// </summary>
public sealed class ExternalAddressPreparationStep(
    IEnumerable<IExternalAddressProvider> providers,
    ILogger<ExternalAddressPreparationStep>? logger = null) : IConnectionPreparationStep
{
    private readonly IReadOnlyList<IExternalAddressProvider> _providers = providers.ToList();
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public int Order => 100;

    public async Task<ConnectionParameters> PrepareAsync(PreparationContext context, ConnectionParameters parameters, CancellationToken ct)
    {
        var connection = context.Connection;
        var kind = connection.ExternalAddressProvider;
        if (kind == ExternalAddressProvider.None)
            return parameters;

        var provider = _providers.FirstOrDefault(p => p.Kind == kind)
            ?? throw new ExternalProviderException($"External address provider {kind} is not available.");
        if (string.IsNullOrWhiteSpace(connection.EC2InstanceId))
            throw new ExternalProviderException($"{provider.DisplayName}: \"{connection.Name}\" uses the {provider.DisplayName} address provider but has no EC2 instance ID.");

        var address = await provider.ResolveAsync(new ExternalAddressRequest(connection.EC2InstanceId, connection.EC2Region), ct);
        _logger.LogInformation("{Provider} resolved {Instance} to {Address}", provider.DisplayName, connection.EC2InstanceId, address);
        return parameters with { Hostname = address };
    }
}

/// <summary>
/// Order 200: fills username / password / domain (and an SSH private key) from the connection's
/// external credential provider, and the RD Gateway credentials from the gateway's provider.
/// Connections without a username and provider use the default provider from the options
/// (legacy "UserViaAPIDefault").
/// </summary>
public sealed class ExternalCredentialPreparationStep(
    IEnumerable<IExternalCredentialProvider> providers,
    Func<AppSettings> settings,
    ILogger<ExternalCredentialPreparationStep>? logger = null) : IConnectionPreparationStep
{
    private readonly IReadOnlyList<IExternalCredentialProvider> _providers = providers.ToList();
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public int Order => 200;

    public async Task<ConnectionParameters> PrepareAsync(PreparationContext context, ConnectionParameters parameters, CancellationToken ct)
    {
        var connection = context.Connection;

        if (!context.Options.NoCredentials)
        {
            var (kind, reference) = (connection.ExternalCredentialProvider, connection.UserViaAPI ?? string.Empty);
            var s = settings();
            if (kind == ExternalCredentialProvider.None && string.IsNullOrEmpty(parameters.Username)
                && string.IsNullOrEmpty(s.DefaultUsername)
                && s.DefaultExternalCredentialProvider != ExternalCredentialProvider.None
                && !string.IsNullOrWhiteSpace(s.DefaultUserViaApi))
            {
                (kind, reference) = (s.DefaultExternalCredentialProvider, s.DefaultUserViaApi);
            }

            if (kind != ExternalCredentialProvider.None)
            {
                var credential = await FetchAsync(kind, new ExternalCredentialRequest
                {
                    Reference = reference,
                    Username = parameters.Username,
                    Hostname = parameters.Hostname,
                    VaultMount = connection.VaultOpenbaoMount ?? string.Empty,
                    VaultRole = connection.VaultOpenbaoRole ?? string.Empty,
                    VaultEngine = connection.VaultOpenbaoSecretEngine,
                }, connection, "connection", ct);
                parameters = await ApplyAsync(context, parameters, credential, ct);
            }
        }

        return await ApplyGatewayAsync(context, parameters, ct);
    }

    private async Task<ExternalCredential> FetchAsync(
        ExternalCredentialProvider kind, ExternalCredentialRequest request, ConnectionInfo connection, string purpose, CancellationToken ct)
    {
        var provider = _providers.FirstOrDefault(p => p.Kind == kind)
            ?? throw new ExternalProviderException($"External credential provider {kind} is not available.");
        var credential = await provider.GetAsync(request, ct);
        _logger.LogInformation("{Provider} supplied the {Purpose} credentials of \"{Connection}\"", provider.DisplayName, purpose, connection.Name);
        return credential;
    }

    private static async Task<ConnectionParameters> ApplyAsync(
        PreparationContext context, ConnectionParameters parameters, ExternalCredential credential, CancellationToken ct)
    {
        parameters = parameters with
        {
            Username = Prefer(credential.Username, parameters.Username),
            Password = Prefer(credential.Password, parameters.Password),
            Domain = Prefer(credential.Domain, parameters.Domain),
        };

        if (!string.IsNullOrEmpty(credential.PrivateKey) && parameters.Protocol is ProtocolType.Ssh or ProtocolType.SshSftp)
        {
            var keyFile = await TemporaryKeyFile.CreateAsync(credential.PrivateKey, ct);
            context.Resources.Add(keyFile);
            parameters = parameters with
            {
                PrivateKeyPath = keyFile.Path,
                PrivateKeyPassphrase = credential.PrivateKeyPassphrase ?? parameters.PrivateKeyPassphrase,
            };
        }
        return parameters;
    }

    private async Task<ConnectionParameters> ApplyGatewayAsync(PreparationContext context, ConnectionParameters parameters, CancellationToken ct)
    {
        var connection = context.Connection;
        if (parameters.Protocol != ProtocolType.Rdp
            || !parameters.Extras.TryGetValue(ConnectionParametersFactory.Keys.RdpGateway, out var gateway)
            || connection.RDGatewayExternalCredentialProvider == ExternalCredentialProvider.None
            || connection.RDGatewayUseConnectionCredentials is not (RDGatewayUseConnectionCredentials.No or RDGatewayUseConnectionCredentials.ExternalCredentialProvider))
            return parameters;

        var credential = await FetchAsync(connection.RDGatewayExternalCredentialProvider, new ExternalCredentialRequest
        {
            Reference = connection.RDGatewayUserViaAPI ?? string.Empty,
            Username = NullIfEmpty(connection.RDGatewayUsername),
            Hostname = gateway,
            VaultMount = connection.VaultOpenbaoMount ?? string.Empty,
            VaultRole = connection.VaultOpenbaoRole ?? string.Empty,
            VaultEngine = connection.VaultOpenbaoSecretEngine,
        }, connection, "RD Gateway", ct);

        var extras = new Dictionary<string, string>(parameters.Extras);
        Set(extras, ConnectionParametersFactory.Keys.RdpGatewayUsername, Prefer(credential.Username, NullIfEmpty(connection.RDGatewayUsername)));
        Set(extras, ConnectionParametersFactory.Keys.RdpGatewayPassword, credential.Password);
        Set(extras, ConnectionParametersFactory.Keys.RdpGatewayDomain, Prefer(credential.Domain, NullIfEmpty(connection.RDGatewayDomain)));
        return parameters with { Extras = extras };
    }

    private static void Set(Dictionary<string, string> extras, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            extras[key] = value;
    }

    private static string? Prefer(string? fromProvider, string? current) =>
        string.IsNullOrEmpty(fromProvider) ? current : fromProvider;

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>
/// A private key from a provider written to a file only the current user can read, for the SSH
/// client; deleted when the session closes (or when preparation fails).
/// </summary>
public sealed class TemporaryKeyFile : IAsyncDisposable
{
    private TemporaryKeyFile(string path) => Path = path;

    public string Path { get; }

    public static async Task<TemporaryKeyFile> CreateAsync(string privateKey, CancellationToken ct)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mremoteng-keys-" + Environment.UserName);
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        DeleteOrphans(directory);
        var path = System.IO.Path.Combine(directory, $"{Environment.ProcessId}-{Guid.NewGuid():N}.key");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        // Key parsers need the PEM lines intact and a final newline.
        var text = privateKey.Replace("\r\n", "\n").Trim('\n', ' ') + "\n";
        await using (var stream = new FileStream(path, options))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            await writer.WriteAsync(text.AsMemory(), ct);
        return new TemporaryKeyFile(path);
    }

    /// <summary>Removes key files left behind by app instances that are no longer running (crash, kill).</summary>
    private static void DeleteOrphans(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.key"))
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(file);
            var dash = name.IndexOf('-');
            if (dash <= 0 || !int.TryParse(name[..dash], out var pid) || IsRunning(pid))
                continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return ValueTask.CompletedTask;
    }
}
