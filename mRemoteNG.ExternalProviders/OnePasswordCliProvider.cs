using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Settings;

namespace mRemoteNG.ExternalProviders;

/// <summary>
/// 1Password through its command-line tool <c>op</c> (which handles sign-in, the desktop-app
/// integration and service-account tokens). References, as in the legacy app:
/// <list type="bullet">
/// <item><c>op://Vault/Item?account=my.1password.com</c> — <c>op item get</c>; username, password,
/// "domain" field and SSH key are taken from the item;</item>
/// <item><c>op://Vault/Item/field</c> (or …/section/field) — <c>op read</c>; the value is the password;</item>
/// <item>a bare item name or ID — <c>op item get</c> in any vault.</item>
/// </list>
/// </summary>
public sealed class OnePasswordCliProvider(Func<AppSettings> settings) : IExternalCredentialProvider
{
    private const string Name = "1Password";

    // Login items mark their fields with a purpose; Server items (and others) only have labels.
    private const string UserNamePurpose = "USERNAME";
    private const string PasswordPurpose = "PASSWORD";
    private const string StringType = "STRING";
    private const string ConcealedType = "CONCEALED";
    private const string SshKeyType = "SSHKEY";

    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    public ExternalCredentialProvider Kind => ExternalCredentialProvider.OnePassword;

    public string DisplayName => Name;

    public async Task<ExternalCredential> GetAsync(ExternalCredentialRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reference = request.Reference?.Trim() ?? string.Empty;
        if (reference.Length == 0)
            throw new ExternalProviderException($"{Name}: the secret reference (UserViaAPI) is empty; use op://Vault/Item.");

        var parsed = Parse(reference, settings().OnePasswordAccount);
        if (parsed.Field is not null)
        {
            var args = new List<string> { "read", "--no-newline", parsed.SecretReference! };
            AddAccount(args, parsed.Account);
            var value = await RunAsync(args, ct);
            if (string.IsNullOrEmpty(value))
                throw new ExternalProviderException($"{Name}: {parsed.SecretReference} is empty.");
            return new ExternalCredential { Password = value };
        }

        var itemArgs = new List<string> { "item", "get", parsed.Item };
        AddAccount(itemArgs, parsed.Account);
        if (!string.IsNullOrEmpty(parsed.Vault))
        {
            itemArgs.Add("--vault");
            itemArgs.Add(parsed.Vault);
        }
        itemArgs.Add("--format");
        itemArgs.Add("json");

        var output = await RunAsync(itemArgs, ct);
        JsonNode? item;
        try
        {
            item = JsonNode.Parse(output);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ExternalProviderException($"{Name}: op item get returned something that is not JSON.", ex);
        }

        var fields = (item?["fields"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var credential = new ExternalCredential
        {
            Username = FindField(fields, UserNamePurpose, "username"),
            Password = FindField(fields, PasswordPurpose, "password"),
            Domain = fields.Where(f => Is(f, "type", StringType) && Is(f, "label", "domain"))
                .Select(f => ProviderHttp.Text(f, "value")).FirstOrDefault(v => v is not null),
            PrivateKey = fields.Where(f => Is(f, "type", SshKeyType)).Select(SshPrivateKey).FirstOrDefault(v => v is not null),
        };
        if (!credential.HasSecret)
            throw new ExternalProviderException(
                $"{Name}: no secret found in \"{parsed.Item}\". The item needs a password field (purpose PASSWORD or label \"password\") or an SSH key.");
        return credential;
    }

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        var args = new List<string> { "whoami", "--format", "json" };
        AddAccount(args, settings().OnePasswordAccount);
        var output = await RunAsync(args, ct);
        try
        {
            var json = JsonNode.Parse(output);
            var email = ProviderHttp.Text(json, "email") ?? ProviderHttp.Text(json, "user_type") ?? "(unknown)";
            var url = ProviderHttp.Text(json, "url");
            return url is null ? $"Signed in as {email}." : $"Signed in to {url} as {email}.";
        }
        catch (System.Text.Json.JsonException)
        {
            return "Signed in: " + output.Trim();
        }
    }

    internal sealed record Reference(string Item, string? Vault, string? Account, string? Field, string? SecretReference);

    /// <summary>Splits a legacy-style reference into its parts (see the class remarks).</summary>
    internal static Reference Parse(string reference, string? defaultAccount)
    {
        var account = string.IsNullOrWhiteSpace(defaultAccount) ? null : defaultAccount.Trim();
        if (!reference.StartsWith("op://", StringComparison.OrdinalIgnoreCase))
            return new Reference(reference, null, account, null, null);

        var withoutScheme = reference["op://".Length..];
        var queryStart = withoutScheme.IndexOf('?');
        var query = queryStart >= 0 ? withoutScheme[(queryStart + 1)..] : string.Empty;
        var path = queryStart >= 0 ? withoutScheme[..queryStart] : withoutScheme;

        var otherQuery = new List<string>();
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = WebUtility.UrlDecode(eq >= 0 ? part[..eq] : part);
            if (key.Equals("account", StringComparison.OrdinalIgnoreCase))
                account = WebUtility.UrlDecode(eq >= 0 ? part[(eq + 1)..] : string.Empty);
            else
                otherQuery.Add(part);
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(WebUtility.UrlDecode).ToList();
        if (segments.Count < 2)
            throw new ExternalProviderException($"{Name}: \"{reference}\" is not a valid reference; use op://Vault/Item or op://Vault/Item/field.");

        if (segments.Count == 2)
            return new Reference(segments[1]!, segments[0], account, null, null);

        // A secret reference: hand it to "op read" without our own account parameter.
        var secretReference = "op://" + path + (otherQuery.Count > 0 ? "?" + string.Join('&', otherQuery) : string.Empty);
        return new Reference(segments[1]!, segments[0], account, segments[^1], secretReference);
    }

    private static void AddAccount(List<string> args, string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
            return;
        args.Add("--account");
        args.Add(account.Trim());
    }

    private static bool Is(JsonObject field, string member, string expected) =>
        string.Equals(ProviderHttp.Text(field, member), expected, StringComparison.OrdinalIgnoreCase);

    private static string? FindField(List<JsonObject> fields, string purpose, string label) =>
        fields.Where(f => Is(f, "purpose", purpose)).Select(f => ProviderHttp.Text(f, "value")).FirstOrDefault(v => v is not null)
        ?? fields.Where(f => (Is(f, "type", StringType) || Is(f, "type", ConcealedType)) && (Is(f, "id", label) || Is(f, "label", label)))
            .Select(f => ProviderHttp.Text(f, "value")).FirstOrDefault(v => v is not null);

    /// <summary>SSH key fields carry PKCS#8 in "value" and an OpenSSH-format copy under ssh_formats.</summary>
    private static string? SshPrivateKey(JsonObject field) =>
        ProviderHttp.Text(field["ssh_formats"]?["openssh"], "value") ?? ProviderHttp.Text(field, "value");

    private string Executable()
    {
        var configured = settings().OnePasswordCliPath;
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();
        return OperatingSystem.IsWindows() ? "op.exe" : "op";
    }

    private async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var executable = Executable();
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        var commandLine = executable + " " + string.Join(' ', args);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new ExternalProviderException(
                $"{Name}: the 1Password CLI (\"{executable}\") could not be started: {ex.Message}. Install it or set its path in Options → External Providers.", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
            {
                var message = string.IsNullOrWhiteSpace(error) ? $"exit code {process.ExitCode}" : error.Trim();
                throw new ExternalProviderException($"{Name}: \"{commandLine}\" failed: {message}");
            }
            return output;
        }
        catch (OperationCanceledException) when (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested)
                throw;
            throw new ExternalProviderException($"{Name}: \"{commandLine}\" did not finish within {Timeout.TotalSeconds:0} seconds (waiting for an unlock prompt?).");
        }
    }
}
