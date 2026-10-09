using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Avalonia.Input;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;
using mRemoteNG.ExternalProviders;
using mRemoteNG.Platform;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.Ssh;
using mRemoteNG.Tests.CrossPlatform.ExternalProviders;
using NSubstitute;
using Xunit;
using CoreProtocol = mRemoteNG.Core.Connection.Protocol.ProtocolType;
using SocketException = System.Net.Sockets.SocketException;
using TcpClient = System.Net.Sockets.TcpClient;

namespace mRemoteNG.Tests.CrossPlatform.Integration;

/// <summary>
/// A throw-away OpenBao (or Vault) dev server on 127.0.0.1:8210 (cluster port 8211), seeded with KV v1/v2 secrets, the SSH
/// OTP engine, a userpass login with a restricted policy and — when slapd is installed — the LDAP
/// secrets engine against a temporary slapd on 127.0.0.1:8390. When running as root with OpenSSH
/// installed, an sshd on 127.0.0.1:8222 accepts the key stored in Vault for the end-to-end test.
/// The server binary is taken from MREMOTENG_BAO_PATH, else "bao" or "vault" on PATH; without one the
/// tests are skipped.
/// </summary>
public sealed class OpenBaoFixture : IAsyncLifetime
{
    public const int BaoPort = 8210; // the dev server also listens on BaoPort + 1 (cluster)
    public const int LdapPort = 8390;
    public const int SshPort = 8222;
    public const string RootToken = "root";
    public const string LdapBaseDn = "dc=example,dc=org";
    public const string LdapAdminDn = "cn=admin," + LdapBaseDn;

    private readonly List<Process> _processes = [];
    private readonly HttpClient _http = new() { BaseAddress = new Uri($"http://127.0.0.1:{BaoPort}/v1/") };

    public string Url => $"http://127.0.0.1:{BaoPort}";
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "mremoteng-bao-" + Guid.NewGuid().ToString("N"));
    public string? SkipReason { get; private set; }
    public string? LdapSkipReason { get; private set; }
    public string? SshSkipReason { get; private set; }
    public string SshHostPublicKey { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var bao = FindBao();
        if (bao is null)
        {
            SkipReason = "OpenBao/Vault binary not found (set MREMOTENG_BAO_PATH or put bao/vault on PATH)";
            LdapSkipReason = SshSkipReason = SkipReason;
            return;
        }
        if (PortOpen(BaoPort) || PortOpen(BaoPort + 1))
        {
            SkipReason = LdapSkipReason = SshSkipReason = $"port {BaoPort} is already in use";
            return;
        }

        Start(bao, "server", "-dev", $"-dev-listen-address=127.0.0.1:{BaoPort}", $"-dev-root-token-id={RootToken}");
        _http.DefaultRequestHeaders.Add("X-Vault-Token", RootToken);
        if (!await WaitAsync(async () => (await _http.GetAsync("sys/health")).IsSuccessStatusCode))
        {
            SkipReason = LdapSkipReason = SshSkipReason = "the dev server did not start";
            return;
        }

        // KV v2 ("secret/" exists in dev mode) with the legacy layout { "<username>": "<password>" }.
        await WriteAsync("secret/data/servers/web01", new JsonObject { ["data"] = new JsonObject { ["root"] = "kv2-pw" } });
        await WriteAsync("secret/data/private/admin", new JsonObject { ["data"] = new JsonObject { ["password"] = "nope" } });
        // KV v1 with conventional keys.
        await WriteAsync("sys/mounts/kv1", new JsonObject { ["type"] = "kv", ["options"] = new JsonObject { ["version"] = "1" } });
        await WriteAsync("kv1/servers/db01", new JsonObject { ["username"] = "dba", ["password"] = "kv1-pw", ["domain"] = "CORP" });
        // SSH one-time passwords.
        await WriteAsync("sys/mounts/ssh", new JsonObject { ["type"] = "ssh" });
        await WriteAsync("ssh/roles/otp", new JsonObject { ["key_type"] = "otp", ["default_user"] = "root", ["cidr_list"] = "127.0.0.0/8" });
        // userpass login limited to the servers/ secrets.
        await WriteAsync("sys/policies/acl/mremote", new JsonObject
        {
            ["policy"] = """
                path "secret/data/servers/*" { capabilities = ["read"] }
                path "kv1/servers/*" { capabilities = ["read"] }
                """,
        });
        await WriteAsync("sys/auth/userpass", new JsonObject { ["type"] = "userpass" });
        await WriteAsync("auth/userpass/users/alice", new JsonObject { ["password"] = "alice-pw", ["policies"] = "mremote" });

        LdapSkipReason = await StartLdapAsync();
        SshSkipReason = await StartSshdAsync();
    }

    private async Task<string?> StartLdapAsync()
    {
        const string slapd = "/usr/sbin/slapd";
        if (!File.Exists(slapd) || FindOnPath("ldapadd") is null || FindOnPath("ldapwhoami") is null
            || !File.Exists("/etc/ldap/schema/inetorgperson.schema"))
            return "OpenLDAP (slapd, ldapadd, ldapwhoami) is not installed";
        if (PortOpen(LdapPort))
            return $"port {LdapPort} is already in use";

        var dir = Path.Combine(Directory, "ldap");
        System.IO.Directory.CreateDirectory(Path.Combine(dir, "db"));
        var config = Path.Combine(dir, "slapd.conf");
        File.WriteAllText(config, $"""
            include /etc/ldap/schema/core.schema
            include /etc/ldap/schema/cosine.schema
            include /etc/ldap/schema/inetorgperson.schema
            modulepath /usr/lib/ldap
            moduleload back_mdb
            pidfile {Path.Combine(dir, "slapd.pid")}
            database mdb
            maxsize 10485760
            suffix "{LdapBaseDn}"
            rootdn "{LdapAdminDn}"
            rootpw admin
            directory {Path.Combine(dir, "db")}
            """);
        // -d 0 keeps slapd in the foreground so it is stopped with the fixture.
        Start(slapd, "-f", config, "-h", $"ldap://127.0.0.1:{LdapPort}/", "-d", "0");
        if (!await WaitAsync(() => Task.FromResult(PortOpen(LdapPort))))
            return "slapd did not start";

        var seed = Path.Combine(dir, "seed.ldif");
        File.WriteAllText(seed, $"""
            dn: {LdapBaseDn}
            objectClass: dcObject
            objectClass: organization
            o: Example
            dc: example

            dn: ou=users,{LdapBaseDn}
            objectClass: organizationalUnit
            ou: users

            dn: uid=svc,ou=users,{LdapBaseDn}
            objectClass: inetOrgPerson
            uid: svc
            cn: svc
            sn: svc
            userPassword: initial

            """);
        if (Run("ldapadd", "-x", "-H", $"ldap://127.0.0.1:{LdapPort}", "-D", LdapAdminDn, "-w", "admin", "-f", seed) != 0)
            return "seeding slapd failed";

        await WriteAsync("sys/mounts/ldap", new JsonObject { ["type"] = "ldap" });
        await WriteAsync("ldap/config", new JsonObject
        {
            ["binddn"] = LdapAdminDn, ["bindpass"] = "admin", ["url"] = $"ldap://127.0.0.1:{LdapPort}", ["schema"] = "openldap",
        });
        await WriteAsync("ldap/static-role/svc", new JsonObject
        {
            ["dn"] = $"uid=svc,ou=users,{LdapBaseDn}", ["username"] = "svc", ["rotation_period"] = "24h",
        });
        await WriteAsync("ldap/role/dyn", new JsonObject
        {
            ["creation_ldif"] = $"""
                dn: uid={"{{.Username}}"},ou=users,{LdapBaseDn}
                objectClass: inetOrgPerson
                uid: {"{{.Username}}"}
                cn: {"{{.Username}}"}
                sn: {"{{.Username}}"}
                userPassword: {"{{.Password}}"}
                """,
            ["deletion_ldif"] = $"""
                dn: uid={"{{.Username}}"},ou=users,{LdapBaseDn}
                changetype: delete
                """,
            ["default_ttl"] = "1h",
        });
        return null;
    }

    private async Task<string?> StartSshdAsync()
    {
        const string sshd = "/usr/sbin/sshd";
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return "sshd tests run on Linux/macOS only";
        if (!File.Exists(sshd) || FindOnPath("ssh-keygen") is null)
            return "OpenSSH server (sshd) or ssh-keygen is not installed";
        if (Environment.UserName != "root")
            return "the sshd end-to-end test must run as root";
        if (PortOpen(SshPort))
            return $"port {SshPort} is already in use";

        var dir = Path.Combine(Directory, "ssh");
        System.IO.Directory.CreateDirectory(dir);
        var hostKey = Path.Combine(dir, "host_ed25519");
        var clientKey = Path.Combine(dir, "client_ed25519");
        if (Run("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-C", "", "-f", hostKey) != 0
            || Run("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-C", "", "-f", clientKey) != 0)
            return "ssh-keygen failed";
        File.Copy(clientKey + ".pub", Path.Combine(dir, "authorized_keys"));
        SshHostPublicKey = File.ReadAllText(hostKey + ".pub").Split(' ')[1];

        var config = Path.Combine(dir, "sshd_config");
        File.WriteAllText(config, $"""
            ListenAddress 127.0.0.1
            PidFile {Path.Combine(dir, "sshd.pid")}
            AuthorizedKeysFile {Path.Combine(dir, "authorized_keys")}
            PermitRootLogin prohibit-password
            PasswordAuthentication no
            KbdInteractiveAuthentication no
            UsePAM no
            StrictModes no
            """);
        System.IO.Directory.CreateDirectory("/run/sshd");
        Start(sshd, "-D", "-e", "-p", SshPort.ToString(), "-f", config, "-h", hostKey);
        if (!await WaitAsync(() => Task.FromResult(PortOpen(SshPort))))
            return "sshd did not start";

        // The private key only exists in Vault from here on.
        await WriteAsync("secret/data/ssh/e2e", new JsonObject
        {
            ["data"] = new JsonObject { ["username"] = "root", ["private_key"] = File.ReadAllText(clientKey) },
        });
        File.Delete(clientKey);
        return null;
    }

    /// <summary>POST to the API as root; throws with the server's message on failure.</summary>
    public async Task<JsonNode?> WriteAsync(string path, JsonObject body)
    {
        using var response = await _http.PostAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"POST {path}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    private static string? FindBao()
    {
        var configured = Environment.GetEnvironmentVariable("MREMOTENG_BAO_PATH");
        if (!string.IsNullOrEmpty(configured))
            return File.Exists(configured) ? configured : null;
        return FindOnPath("bao") ?? FindOnPath("vault");
    }

    internal static string? FindOnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(dir => dir.Length > 0)
            .Select(dir => Path.Combine(dir, tool))
            .FirstOrDefault(File.Exists);

    private void Start(string file, params string[] args)
    {
        var startInfo = new ProcessStartInfo(file) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        var process = Process.Start(startInfo)!;
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        _processes.Add(process);
    }

    internal static int Run(string file, params string[] args)
    {
        var startInfo = new ProcessStartInfo(file) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode;
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await ready())
                    return true;
            }
            catch (HttpRequestException)
            {
            }
            await Task.Delay(100);
        }
        return false;
    }

    private static bool PortOpen(int port)
    {
        try
        {
            using var tcp = new TcpClient();
            tcp.Connect("127.0.0.1", port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public Task DisposeAsync()
    {
        foreach (var process in Enumerable.Reverse(_processes))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
            }
            process.Dispose();
        }
        _http.Dispose();
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { }
        return Task.CompletedTask;
    }
}

/// <summary>
/// The Vault/OpenBao provider against a real server: every legacy secret engine, the login methods,
/// and an SSH session whose private key comes from Vault.
/// Run only these with: dotnet test --filter Category=Integration
/// </summary>
[Trait("Category", "Integration")]
public sealed class VaultOpenbaoIntegrationTests(OpenBaoFixture bao) : IClassFixture<OpenBaoFixture>
{
    private readonly AppSettings _settings = new()
    {
        VaultUrl = bao.Url,
        VaultSecretProtected = TestSecrets.Protect(OpenBaoFixture.RootToken),
    };

    private VaultOpenbaoProvider Provider() =>
        new(() => _settings, new DefaultProviderHttpClientFactory(), TestSecrets.Create());

    private static ExternalCredentialRequest Request(VaultOpenbaoSecretEngine engine, string mount, string role, string? username = null, string? host = null) =>
        new() { VaultEngine = engine, VaultMount = mount, VaultRole = role, Username = username, Hostname = host };

    [SkippableFact]
    public async Task Kv2_LegacyLayout_WithRootToken()
    {
        Skip.If(bao.SkipReason is not null, bao.SkipReason);

        var credential = await Provider().GetAsync(Request(VaultOpenbaoSecretEngine.Kv, "secret", "servers/web01", "root"));

        credential.Should().BeEquivalentTo(new ExternalCredential { Password = "kv2-pw" });
    }

    [SkippableFact]
    public async Task Kv1_WithUserPassLogin_AndARestrictedPolicy()
    {
        Skip.If(bao.SkipReason is not null, bao.SkipReason);
        _settings.VaultAuthMethod = VaultAuthMethod.UserPass;
        _settings.VaultUsername = "alice";
        _settings.VaultSecretProtected = TestSecrets.Protect("alice-pw");

        var credential = await Provider().GetAsync(Request(VaultOpenbaoSecretEngine.Kv, "kv1", "servers/db01"));

        credential.Should().BeEquivalentTo(new ExternalCredential { Username = "dba", Password = "kv1-pw", Domain = "CORP" });
    }

    [SkippableFact]
    public async Task PolicyDenial_IsReportedClearly()
    {
        Skip.If(bao.SkipReason is not null, bao.SkipReason);
        _settings.VaultAuthMethod = VaultAuthMethod.UserPass;
        _settings.VaultUsername = "alice";
        _settings.VaultSecretProtected = TestSecrets.Protect("alice-pw");

        var act = () => Provider().GetAsync(Request(VaultOpenbaoSecretEngine.Kv, "secret", "private/admin", "root"));

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("Vault/OpenBao: reading secret/private/admin failed (403 Forbidden): *permission denied*");
    }

    [SkippableFact]
    public async Task WrongUserPassPassword_IsReported()
    {
        Skip.If(bao.SkipReason is not null, bao.SkipReason);
        _settings.VaultAuthMethod = VaultAuthMethod.UserPass;
        _settings.VaultUsername = "alice";
        _settings.VaultSecretProtected = TestSecrets.Protect("wrong");

        var act = () => Provider().TestAsync();

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("Vault/OpenBao: login (UserPass, userpass) failed (400 Bad Request): invalid*");
    }

    [SkippableFact]
    public async Task SshOtp_IsIssuedForTheHost()
    {
        Skip.If(bao.SkipReason is not null, bao.SkipReason);

        var credential = await Provider().GetAsync(Request(VaultOpenbaoSecretEngine.SSHOTP, "ssh", "otp", "root", "localhost"));

        credential.Username.Should().Be("root");
        Guid.TryParse(credential.Password, out _).Should().BeTrue("OpenBao OTPs are UUIDs, got {0}", credential.Password);
    }

    [SkippableFact]
    public async Task LdapStatic_ReturnsTheRotatedPassword_WhichBindsToTheDirectory()
    {
        Skip.If(bao.LdapSkipReason is not null, bao.LdapSkipReason);

        var credential = await Provider().GetAsync(Request(VaultOpenbaoSecretEngine.LdapStatic, "ldap", "svc"));

        credential.Username.Should().Be("svc");
        Bind($"uid=svc,ou=users,{OpenBaoFixture.LdapBaseDn}", credential.Password!).Should().Be(0);
    }

    [SkippableFact]
    public async Task LdapDynamic_CreatesAnAccount_WhichBindsToTheDirectory()
    {
        Skip.If(bao.LdapSkipReason is not null, bao.LdapSkipReason);

        var credential = await Provider().GetAsync(Request(VaultOpenbaoSecretEngine.LdapDynamic, "ldap", "dyn"));

        credential.Username.Should().StartWith("v_");
        Bind($"uid={credential.Username},ou=users,{OpenBaoFixture.LdapBaseDn}", credential.Password!).Should().Be(0);
    }

    [SkippableFact]
    public async Task TestAsync_ReportsTheTokenIdentity()
    {
        Skip.If(bao.SkipReason is not null, bao.SkipReason);

        var message = await Provider().TestAsync();

        message.Should().Be($"Connected to {bao.Url} as token (policies: root).");
    }

    [SkippableFact]
    public async Task EndToEnd_SshConnectionUsesTheKeyStoredInVault()
    {
        Skip.If(bao.SshSkipReason is not null, bao.SshSkipReason);

        // The app's wiring: settings + crypto + AddExternalProviders feed the ConnectionPreparer.
        var services = new ServiceCollection();
        var settingsService = new AppSettingsService(Substitute.For<ISettingsProvider>());
        settingsService.Update(s =>
        {
            s.VaultUrl = bao.Url;
            s.VaultSecretProtected = TestSecrets.Protect(OpenBaoFixture.RootToken);
        });
        services.AddSingleton(settingsService);
        services.AddSingleton(TestSecrets.Crypto);
        services.AddExternalProviders();
        services.AddSingleton<ConnectionPreparer>();
        using var provider = services.BuildServiceProvider();

        var connection = new ConnectionInfo
        {
            Name = "vault-e2e",
            Hostname = "127.0.0.1",
            Port = OpenBaoFixture.SshPort,
            Protocol = CoreProtocol.SSH2,
            ExternalCredentialProvider = ExternalCredentialProvider.VaultOpenbao,
            VaultOpenbaoSecretEngine = VaultOpenbaoSecretEngine.Kv,
            VaultOpenbaoMount = "secret",
            VaultOpenbaoRole = "ssh/e2e",
        };

        string keyPath;
        await using (var prepared = await provider.GetRequiredService<ConnectionPreparer>().PrepareAsync(connection))
        {
            prepared.Parameters.Username.Should().Be("root");
            keyPath = prepared.Parameters.PrivateKeyPath!;
            File.Exists(keyPath).Should().BeTrue();

            var knownHosts = Path.Combine(bao.Directory, "known_hosts");
            var store = new KnownHostsStore(knownHosts);
            store.Add(new HostKeyInfo("127.0.0.1", OpenBaoFixture.SshPort, Convert.FromBase64String(bao.SshHostPublicKey)));
            var prompt = Substitute.For<ISshUserPrompt>();
            using var protocol = new SshNetProtocol(NullLogger<SshNetProtocol>.Instance, new KnownHostsHostKeyVerifier(store, prompt), prompt);
            var view = (TerminalView)protocol.CreateView();

            await protocol.ConnectAsync(prepared.Parameters);
            protocol.State.Should().Be(ConnectionState.Connected);
            view.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "echo vault-e2e-$((6*7))-$(id -un)\r" });
            await WaitForScreenAsync(view, "vault-e2e-42-root");
            await protocol.DisconnectAsync();

            // No password prompt was needed: the key from Vault authenticated the session.
            await prompt.DidNotReceiveWithAnyArgs().PromptTextAsync(default!, default);
        }

        File.Exists(keyPath).Should().BeFalse("the temporary key file is deleted when the session's resources are released");
    }

    private static int Bind(string dn, string password) =>
        OpenBaoFixture.Run("ldapwhoami", "-x", "-H", $"ldap://127.0.0.1:{OpenBaoFixture.LdapPort}", "-D", dn, "-w", password);

    private static async Task WaitForScreenAsync(TerminalView view, string text)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!view.GetScreenText().Contains(text, StringComparison.Ordinal))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"'{text}' did not appear. Screen:\n{view.GetScreenText()}");
            await Task.Delay(50);
        }
    }
}
