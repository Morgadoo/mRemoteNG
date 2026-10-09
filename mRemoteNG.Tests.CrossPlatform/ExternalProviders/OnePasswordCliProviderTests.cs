using FluentAssertions;
using mRemoteNG.Core.Settings;
using mRemoteNG.ExternalProviders;
using Xunit;

namespace mRemoteNG.Tests.CrossPlatform.ExternalProviders;

/// <summary>
/// Runs the provider against a fake <c>op</c> shell script that records its arguments and prints
/// canned output in the shape of the real 1Password CLI 2.x.
/// </summary>
[Collection(OnePasswordPathCollection.Name)]
public sealed class OnePasswordCliProviderTests : IDisposable
{
    // "op item get --format json" for a Login item.
    private const string LoginItemJson = """
        {
          "id": "kx3n5hd2yhvmzcbrtw4iy2gqyq", "title": "web01", "version": 3,
          "vault": {"id": "tdxpb7gvqblmvbyhnnbvk3pm3e", "name": "Servers"},
          "category": "LOGIN", "last_edited_by": "IKPU6UDEOVBFVAAVBVC4FYWU6Y",
          "created_at": "2025-01-14T09:12:44Z", "updated_at": "2026-03-02T16:40:01Z",
          "sections": [{"id": "add more"}],
          "fields": [
            {"id": "username", "type": "STRING", "purpose": "USERNAME", "label": "username", "value": "admin",
             "reference": "op://Servers/web01/username"},
            {"id": "password", "type": "CONCEALED", "purpose": "PASSWORD", "label": "password", "value": "op-Secret-1",
             "entropy": 115.6, "reference": "op://Servers/web01/password",
             "password_details": {"entropy": 115, "generated": true, "strength": "FANTASTIC"}},
            {"id": "notesPlain", "type": "STRING", "purpose": "NOTES", "label": "notesPlain", "reference": "op://Servers/web01/notesPlain"},
            {"id": "w2fdgsbmqzyyddtlhbcuzcmm5u", "section": {"id": "add more"}, "type": "STRING", "label": "domain",
             "value": "CORP", "reference": "op://Servers/web01/add more/domain"}
          ]
        }
        """;

    // A Server item: built-in username/password fields without a purpose.
    private const string ServerItemJson = """
        {
          "id": "q2u3", "title": "db01", "category": "SERVER", "vault": {"id": "v", "name": "Servers"},
          "fields": [
            {"id": "notesPlain", "type": "STRING", "purpose": "NOTES", "label": "notesPlain"},
            {"id": "url", "type": "STRING", "label": "URL", "value": "db01.example"},
            {"id": "username", "type": "STRING", "label": "username", "value": "postgres"},
            {"id": "password", "type": "CONCEALED", "label": "password", "value": "pg-pw"}
          ]
        }
        """;

    // An SSH Key item: the private key is PKCS#8 in "value" with an OpenSSH copy under ssh_formats.
    private const string SshKeyItemJson = """
        {
          "id": "s5h", "title": "deploy key", "category": "SSH_KEY", "vault": {"id": "v", "name": "Servers"},
          "fields": [
            {"id": "private_key", "type": "SSHKEY", "label": "private key",
             "value": "-----BEGIN PRIVATE KEY-----\nMC4CAQ\n-----END PRIVATE KEY-----\n",
             "reference": "op://Servers/deploy key/private key",
             "ssh_formats": {"openssh": {"reference": "op://Servers/deploy key/private key?ssh-format=openssh",
                                         "value": "-----BEGIN OPENSSH PRIVATE KEY-----\nb3Bl\n-----END OPENSSH PRIVATE KEY-----\n"}}},
            {"id": "public_key", "type": "STRING", "label": "public key", "value": "ssh-ed25519 AAAAC3Nza"},
            {"id": "fingerprint", "type": "STRING", "label": "fingerprint", "value": "SHA256:abc"}
          ]
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mremoteng-op-" + Guid.NewGuid().ToString("N"));
    private readonly AppSettings _settings = new();

    public OnePasswordCliProviderTests()
    {
        Directory.CreateDirectory(_dir);
        if (OperatingSystem.IsWindows())
            return;

        // Records the arguments one per line, then answers like op would.
        var script = Path.Combine(_dir, "op");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            dir="{{_dir}}"
            printf '%s\n' "$@" > "$dir/args.txt"
            if [ -f "$dir/fail.txt" ]; then cat "$dir/fail.txt" >&2; exit 1; fi
            case "$1" in
              item) cat "$dir/item.json" ;;
              read) printf '%s' 'read-only-secret' ;;
              whoami) echo '{"url":"https://my.1password.com","email":"me@example.com","user_uuid":"U","account_uuid":"A","user_type":"HUMAN"}' ;;
            esac
            """.Replace("\r\n", "\n"));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _settings.OnePasswordCliPath = script;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private OnePasswordCliProvider Create() => new(() => _settings);

    private string[] Args() => File.ReadAllLines(Path.Combine(_dir, "args.txt"));

    private void Item(string json) => File.WriteAllText(Path.Combine(_dir, "item.json"), json);

    [SkippableFact]
    public async Task ItemReference_RunsItemGet_WithVaultAndAccount_AndMapsTheLoginFields()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a shell script as fake op");
        Item(LoginItemJson);

        var credential = await Create().GetAsync("op://Servers/web01?account=my.1password.com");

        credential.Should().BeEquivalentTo(new ExternalCredential { Username = "admin", Password = "op-Secret-1", Domain = "CORP" });
        Args().Should().Equal("item", "get", "web01", "--account", "my.1password.com", "--vault", "Servers", "--format", "json");
    }

    [SkippableFact]
    public async Task ServerItem_FallsBackToTheFieldLabels_AndTheDefaultAccount()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a shell script as fake op");
        Item(ServerItemJson);
        _settings.OnePasswordAccount = "team.1password.com";

        var credential = await Create().GetAsync("op://My%20Vault/db01");

        credential.Username.Should().Be("postgres");
        credential.Password.Should().Be("pg-pw");
        Args().Should().Equal("item", "get", "db01", "--account", "team.1password.com", "--vault", "My Vault", "--format", "json");
    }

    [SkippableFact]
    public async Task SshKeyItem_ReturnsTheOpenSshFormattedKey()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a shell script as fake op");
        Item(SshKeyItemJson);

        var credential = await Create().GetAsync("op://Servers/deploy key");

        credential.PrivateKey.Should().StartWith("-----BEGIN OPENSSH PRIVATE KEY-----");
        credential.Password.Should().BeNull();
    }

    [SkippableFact]
    public async Task FieldReference_UsesOpRead()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a shell script as fake op");

        var credential = await Create().GetAsync("op://Servers/web01/password?account=my.1password.com");

        credential.Password.Should().Be("read-only-secret");
        Args().Should().Equal("read", "--no-newline", "op://Servers/web01/password", "--account", "my.1password.com");
    }

    [SkippableFact]
    public async Task BareItemName_SearchesAllVaults()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a shell script as fake op");
        Item(LoginItemJson);

        await Create().GetAsync("web01");

        Args().Should().Equal("item", "get", "web01", "--format", "json");
    }

    [SkippableFact]
    public async Task ItemWithoutSecret_IsReported()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a shell script as fake op");
        Item("""{"id":"x","title":"empty","category":"SECURE_NOTE","fields":[{"id":"notesPlain","type":"STRING","purpose":"NOTES","label":"notesPlain","value":"hi"}]}""");

        var act = () => Create().GetAsync("op://Servers/empty");

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("1Password: no secret found in \"empty\"*");
    }

    [SkippableFact]
    public async Task CliError_IsReportedWithItsMessageAndCommandLine()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a shell script as fake op");
        File.WriteAllText(Path.Combine(_dir, "fail.txt"),
            "[ERROR] 2026/10/09 11:20:01 \"nope\" isn't an item. Specify the item with its UUID, name, or domain.\n");

        var act = () => Create().GetAsync("op://Servers/nope");

        (await act.Should().ThrowAsync<ExternalProviderException>())
            .WithMessage("1Password: \"*op item get nope --vault Servers --format json\" failed: [ERROR] * \"nope\" isn't an item.*");
    }

    [Fact]
    public async Task MissingCli_PointsToTheOptions()
    {
        _settings.OnePasswordCliPath = Path.Combine(_dir, "does-not-exist", "op");

        var act = () => Create().GetAsync("op://Servers/web01");

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("1Password: the 1Password CLI*could not be started*Options → External Providers.");
    }

    [Theory]
    [InlineData("op://OnlyVault")]
    [InlineData("op://")]
    public async Task MalformedReference_IsRejected(string reference)
    {
        var act = () => Create().GetAsync(reference);

        (await act.Should().ThrowAsync<ExternalProviderException>()).WithMessage("*not a valid reference*");
    }

    [SkippableFact]
    public async Task TestAsync_RunsWhoami()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a shell script as fake op");

        var message = await Create().TestAsync();

        message.Should().Be("Signed in to https://my.1password.com as me@example.com.");
        Args().Should().Equal("whoami", "--format", "json");
    }

    [SkippableFact]
    public async Task DefaultExecutable_IsFoundOnPath()
    {
        Skip.If(OperatingSystem.IsWindows(), "uses a shell script as fake op");
        Item(LoginItemJson);
        _settings.OnePasswordCliPath = string.Empty;
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", _dir + Path.PathSeparator + originalPath);
        try
        {
            var credential = await Create().GetAsync("op://Servers/web01");

            credential.Password.Should().Be("op-Secret-1");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }
}

/// <summary>Keeps the test that changes PATH from running alongside the other op tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OnePasswordPathCollection
{
    public const string Name = "1Password CLI (PATH)";
}
