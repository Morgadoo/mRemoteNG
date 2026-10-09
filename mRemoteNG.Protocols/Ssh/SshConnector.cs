using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>
/// Shared connection logic for SSH shell and SFTP clients: resolves the username, builds the
/// authentication methods and connects with host key verification.
/// </summary>
internal sealed class SshConnector
{
    /// <summary>SSH.NET's per-operation timeout, which also bounds the key exchange. Tests shorten it.</summary>
    internal static TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private readonly IHostKeyVerifier _verifier;
    private readonly ISshUserPrompt _prompt;
    private readonly ILogger _logger;

    public SshConnector(IHostKeyVerifier verifier, ISshUserPrompt prompt, ILogger logger)
    {
        _verifier = verifier;
        _prompt = prompt;
        _logger = logger;
    }

    /// <summary>
    /// Connects a new client created by <paramref name="createClient"/>. The returned client is
    /// connected and authenticated; the caller owns it.
    /// </summary>
    /// <exception cref="HostKeyVerificationException">The host key was not trusted.</exception>
    /// <exception cref="InvalidOperationException">No username was configured or entered.</exception>
    public async Task<TClient> ConnectAsync<TClient>(
        ConnectionParameters parameters,
        Func<ConnectionInfo, TClient> createClient,
        CancellationToken ct)
        where TClient : BaseClient
    {
        var username = parameters.Username;
        if (string.IsNullOrWhiteSpace(username))
        {
            username = await _prompt.PromptTextAsync(
                new SshTextPrompt(
                    "Username required",
                    $"No username is configured for {parameters.DisplayName}. Enter the user to log in as.",
                    IsSecret: false,
                    Watermark: "Username"),
                ct);
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new InvalidOperationException(
                    $"No username is configured for {parameters.DisplayName}. Set a username on the connection.");
            }
            username = username.Trim();
        }

        var authMethods = await BuildAuthMethodsAsync(parameters, username, ct);

        // A user may take longer to inspect an unknown host key than SSH.NET's key-exchange timeout.
        // If the key was approved but the handshake timed out meanwhile, reconnect once; the approved
        // key is then trusted without asking again.
        for (int attempt = 1; ; attempt++)
        {
            var connectionInfo = new ConnectionInfo(parameters.Hostname, parameters.Port, username, [.. authMethods])
            {
                Timeout = GetSeconds(parameters, ConnectionParametersFactory.Keys.ConnectTimeoutSeconds) is > 0 and var timeout
                    ? TimeSpan.FromSeconds(timeout)
                    : OperationTimeout,
            };
            PreferKnownHostKeyTypes(connectionInfo, parameters);

            var client = createClient(connectionInfo);
            HostKeyVerdict? verdict = null;
            client.HostKeyReceived += (_, e) =>
            {
                try
                {
                    var key = new HostKeyInfo(parameters.Hostname, parameters.Port, e.HostKey);
                    verdict = _verifier.Verify(key);
                }
                catch (Exception ex)
                {
                    verdict = HostKeyVerdict.Reject("Host key verification failed: " + ex.Message);
                }
                e.CanTrust = verdict.Value.IsTrusted;
            };

            try
            {
                using (ct.Register(client.Dispose))
                    await Task.Run(client.Connect, ct);
                if (GetSeconds(parameters, ConnectionParametersFactory.Keys.SshKeepAliveSeconds) is > 0 and var keepAlive)
                    client.KeepAliveInterval = TimeSpan.FromSeconds(keepAlive);
                _logger.LogDebug("SSH connected to {Host}: {Reason}", parameters.DisplayName, verdict?.Reason);
                return client;
            }
            catch (Exception ex)
            {
                client.Dispose();
                ct.ThrowIfCancellationRequested();

                if (verdict is { IsTrusted: false } rejected)
                    throw new HostKeyVerificationException(rejected.Reason, ex);
                if (ex is SshOperationTimeoutException && verdict is { IsTrusted: true } && attempt == 1)
                {
                    _logger.LogInformation("SSH handshake timed out while the host key was confirmed; retrying");
                    continue;
                }
                throw;
            }
        }
    }

    private static int? GetSeconds(ConnectionParameters parameters, string key) =>
        parameters.Extras.TryGetValue(key, out var value)
        && int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;

    /// <summary>
    /// Restricts host key algorithms to plain keys (certificates are not verified) and moves
    /// algorithms for key types already in known_hosts to the front, as OpenSSH does, so that a
    /// known host does not suddenly present a different, unknown key type.
    /// </summary>
    private void PreferKnownHostKeyTypes(ConnectionInfo connectionInfo, ConnectionParameters parameters)
    {
        var known = _verifier.GetKnownKeyTypes(parameters.Hostname, parameters.Port);
        var algorithms = connectionInfo.HostKeyAlgorithms.ToList();
        connectionInfo.HostKeyAlgorithms.Clear();

        var plain = algorithms.Where(a => !a.Key.Contains("-cert-", StringComparison.Ordinal)).ToList();
        foreach (var algorithm in plain.Where(a => known.Contains(KeyTypeOf(a.Key))))
            connectionInfo.HostKeyAlgorithms.Add(algorithm.Key, algorithm.Value);
        foreach (var algorithm in plain.Where(a => !known.Contains(KeyTypeOf(a.Key))))
            connectionInfo.HostKeyAlgorithms.Add(algorithm.Key, algorithm.Value);
    }

    /// <summary>Maps a host key algorithm name to the key type stored in known_hosts.</summary>
    internal static string KeyTypeOf(string algorithm) =>
        algorithm is "rsa-sha2-256" or "rsa-sha2-512" ? "ssh-rsa" : algorithm;

    private async Task<List<AuthenticationMethod>> BuildAuthMethodsAsync(
        ConnectionParameters p, string username, CancellationToken ct)
    {
        var methods = new List<AuthenticationMethod>();

        // 1. Explicitly specified private key, otherwise the default keys in ~/.ssh/
        var keyFiles = new List<IPrivateKeySource>();
        if (!string.IsNullOrEmpty(p.PrivateKeyPath))
        {
            var key = LoadPrivateKey(p.PrivateKeyPath, p.PrivateKeyPassphrase);
            if (key is null)
                _logger.LogWarning("Private key {Path} could not be loaded", p.PrivateKeyPath);
            else
                keyFiles.Add(key);
        }
        if (keyFiles.Count == 0)
            keyFiles.AddRange(DiscoverDefaultKeys(p.PrivateKeyPassphrase));
        if (keyFiles.Count > 0)
            methods.Add(new PrivateKeyAuthenticationMethod(username, [.. keyFiles]));

        // 2. Password; ask for one when nothing else can authenticate.
        var password = p.Password;
        if (string.IsNullOrEmpty(password) && keyFiles.Count == 0)
        {
            password = await _prompt.PromptTextAsync(
                new SshTextPrompt(
                    "Password required",
                    $"Enter the password for {username}@{p.Hostname}.",
                    IsSecret: true,
                    Watermark: "Password"),
                ct);
        }
        if (!string.IsNullOrEmpty(password))
            methods.Add(new PasswordAuthenticationMethod(username, password));

        // 3. Keyboard-interactive: answer with the password, or ask the user (2FA/OTP prompts).
        var keyboard = new KeyboardInteractiveAuthenticationMethod(username);
        keyboard.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                prompt.Response = !string.IsNullOrEmpty(password) && !prompt.IsEchoed
                    ? password
                    : AskBlocking(new SshTextPrompt(
                        string.IsNullOrEmpty(e.Instruction) ? "Authentication" : e.Instruction,
                        prompt.Request,
                        IsSecret: !prompt.IsEchoed)) ?? string.Empty;
            }
        };
        methods.Add(keyboard);

        return methods;
    }

    // Keyboard-interactive prompts are raised on SSH.NET's background thread during Connect().
    private string? AskBlocking(SshTextPrompt prompt) =>
        _prompt.PromptTextAsync(prompt).GetAwaiter().GetResult();

    /// <summary>Loads id_ed25519, id_ecdsa and id_rsa from ~/.ssh/ when present.</summary>
    private List<IPrivateKeySource> DiscoverDefaultKeys(string? passphrase)
    {
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var keys = new List<IPrivateKeySource>();
        if (!Directory.Exists(sshDir))
            return keys;

        foreach (var name in new[] { "id_ed25519", "id_ecdsa", "id_rsa" })
        {
            var key = LoadPrivateKey(Path.Combine(sshDir, name), passphrase);
            if (key is not null)
                keys.Add(key);
        }
        return keys;
    }

    private PrivateKeyFile? LoadPrivateKey(string path, string? passphrase)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, passphrase);
        }
        catch (Exception ex)
        {
            // Unsupported format or encrypted with a passphrase we don't have.
            _logger.LogDebug(ex, "Skipping private key {Path}", path);
            return null;
        }
    }
}
