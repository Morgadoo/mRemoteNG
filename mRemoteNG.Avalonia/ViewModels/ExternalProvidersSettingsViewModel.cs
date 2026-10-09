using System.Reactive;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Localization;
using mRemoteNG.Core.Settings;
using mRemoteNG.ExternalProviders;
using ReactiveUI;

namespace mRemoteNG.Avalonia.ViewModels;

/// <summary>
/// A provider secret on the Options page: typing a value saves it encrypted (ICryptoProvider) in the
/// working copy; "Forget" clears it so the provider asks once per session instead.
/// </summary>
public sealed class ProviderSecretViewModel : ReactiveObject
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly ProviderSecrets? _secrets;
    private string _typed = string.Empty;

    public ProviderSecretViewModel(Func<string> get, Action<string> set, ProviderSecrets? secrets)
    {
        _get = get;
        _set = set;
        _secrets = secrets;
        ForgetCommand = ReactiveCommand.Create(Forget, this.WhenAnyValue(x => x.IsSaved));
    }

    /// <summary>False when no encryption service is available (secrets are then always asked for).</summary>
    public bool CanSave => _secrets is not null;

    /// <summary>The value typed in this window (the saved value is never shown).</summary>
    public string Value
    {
        get => _typed;
        set
        {
            value ??= string.Empty;
            if (_typed == value)
                return;
            this.RaiseAndSetIfChanged(ref _typed, value);
            if (_secrets is not null)
                _set(_secrets.Protect(value));
            RaiseSaved();
        }
    }

    public bool IsSaved => _get().Length > 0;

    /// <summary>Shown as the input's placeholder; kept short so the page fits the Options window.</summary>
    public string State => IsSaved ? Localizer.Get("SecretSavedEncrypted") : Localizer.Get("SecretNotSaved");

    public ReactiveCommand<Unit, Unit> ForgetCommand { get; }

    private void Forget()
    {
        _typed = string.Empty;
        this.RaisePropertyChanged(nameof(Value));
        _set(string.Empty);
        RaiseSaved();
    }

    private void RaiseSaved()
    {
        this.RaisePropertyChanged(nameof(IsSaved));
        this.RaisePropertyChanged(nameof(State));
    }
}

/// <summary>"Test" button of one provider: runs its check against the unsaved working copy.</summary>
public sealed class ProviderTestViewModel : ReactiveObject
{
    private string _status = string.Empty;
    private bool _failed;

    public ProviderTestViewModel(Func<CancellationToken, Task<string>>? test)
    {
        IsAvailable = test is not null;
        TestCommand = ReactiveCommand.CreateFromTask(async ct =>
        {
            if (test is null)
                return;
            Failed = false;
            Status = Localizer.Get("TestingEllipsis");
            try
            {
                Status = await test(ct);
            }
            catch (ExternalProviderException ex)
            {
                Failed = true;
                Status = ex.Message;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Failed = true;
                Status = Localizer.Format("UnexpectedErrorFormat", ex.Message);
            }
        });
    }

    public bool IsAvailable { get; }

    public ReactiveCommand<Unit, Unit> TestCommand { get; }

    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public bool Failed
    {
        get => _failed;
        private set => this.RaiseAndSetIfChanged(ref _failed, value);
    }
}

/// <summary>Options → External Providers: credential providers, the AWS address provider and their tests.</summary>
public sealed class ExternalProvidersSettingsViewModel : SettingsPageViewModel
{
    public ExternalProvidersSettingsViewModel(AppSettings working, ExternalProviderFactory? providers) : base(working)
    {
        var secrets = providers?.Secrets;
        DelineaPassword = new ProviderSecretViewModel(() => Working.DelineaPasswordProtected, v => Working.DelineaPasswordProtected = v, secrets);
        PasswordstateApiKey = new ProviderSecretViewModel(() => Working.PasswordstateApiKeyProtected, v => Working.PasswordstateApiKeyProtected = v, secrets);
        VaultSecret = new ProviderSecretViewModel(() => Working.VaultSecretProtected, v => Working.VaultSecretProtected = v, secrets);
        AwsSecretAccessKey = new ProviderSecretViewModel(() => Working.AwsSecretAccessKeyProtected, v => Working.AwsSecretAccessKeyProtected = v, secrets);

        Func<CancellationToken, Task<string>>? Test(ExternalCredentialProvider kind) =>
            providers is null ? null : ct => providers.CreateCredentialProvider(kind, () => Working).TestAsync(ct);

        DelineaTest = new ProviderTestViewModel(Test(ExternalCredentialProvider.DelineaSecretServer));
        PasswordstateTest = new ProviderTestViewModel(Test(ExternalCredentialProvider.ClickstudiosPasswordState));
        OnePasswordTest = new ProviderTestViewModel(Test(ExternalCredentialProvider.OnePassword));
        VaultTest = new ProviderTestViewModel(Test(ExternalCredentialProvider.VaultOpenbao));
        AwsTest = new ProviderTestViewModel(providers is null
            ? null
            : ct => providers.CreateAddressProvider(ExternalAddressProvider.AmazonWebServices, () => Working).TestAsync(ct));
    }

    // ── Default provider ──────────────────────────────────────────────────

    public IReadOnlyList<Choice<ExternalCredentialProvider>> CredentialProviders { get; } =
    [
        new(ExternalCredentialProvider.None, Localizer.Get("None")),
        new(ExternalCredentialProvider.DelineaSecretServer, "Delinea Secret Server"),
        new(ExternalCredentialProvider.ClickstudiosPasswordState, "Passwordstate"),
        new(ExternalCredentialProvider.OnePassword, "1Password"),
        new(ExternalCredentialProvider.VaultOpenbao, "Vault/OpenBao"),
    ];

    public Choice<ExternalCredentialProvider> SelectedDefaultProvider
    {
        get => CredentialProviders.FirstOrDefault(c => c.Value == Working.DefaultExternalCredentialProvider) ?? CredentialProviders[0];
        set
        {
            if (value is null) return;
            Set(Working.DefaultExternalCredentialProvider, value.Value, v => Working.DefaultExternalCredentialProvider = v);
            this.RaisePropertyChanged(nameof(HasDefaultProvider));
        }
    }

    public bool HasDefaultProvider => Working.DefaultExternalCredentialProvider != ExternalCredentialProvider.None;

    public string DefaultUserViaApi
    {
        get => Working.DefaultUserViaApi;
        set => Set(Working.DefaultUserViaApi, value?.Trim() ?? string.Empty, v => Working.DefaultUserViaApi = v);
    }

    // ── Delinea Secret Server ─────────────────────────────────────────────

    public string DelineaUrl
    {
        get => Working.DelineaUrl;
        set => Set(Working.DelineaUrl, value?.Trim() ?? string.Empty, v => Working.DelineaUrl = v);
    }

    public string DelineaUsername
    {
        get => Working.DelineaUsername;
        set => Set(Working.DelineaUsername, value?.Trim() ?? string.Empty, v => Working.DelineaUsername = v);
    }

    public string DelineaDomain
    {
        get => Working.DelineaDomain;
        set => Set(Working.DelineaDomain, value?.Trim() ?? string.Empty, v => Working.DelineaDomain = v);
    }

    public bool DelineaUseSso
    {
        get => Working.DelineaUseSso;
        set
        {
            Set(Working.DelineaUseSso, value, v => Working.DelineaUseSso = v);
            this.RaisePropertyChanged(nameof(DelineaUsesLogin));
        }
    }

    public bool DelineaUsesLogin => !Working.DelineaUseSso;

    public bool DelineaRequireOtp
    {
        get => Working.DelineaRequireOtp;
        set => Set(Working.DelineaRequireOtp, value, v => Working.DelineaRequireOtp = v);
    }

    public ProviderSecretViewModel DelineaPassword { get; }

    public ProviderTestViewModel DelineaTest { get; }

    // ── Passwordstate ─────────────────────────────────────────────────────

    public string PasswordstateUrl
    {
        get => Working.PasswordstateUrl;
        set => Set(Working.PasswordstateUrl, value?.Trim() ?? string.Empty, v => Working.PasswordstateUrl = v);
    }

    public bool PasswordstateUseSso
    {
        get => Working.PasswordstateUseSso;
        set
        {
            Set(Working.PasswordstateUseSso, value, v => Working.PasswordstateUseSso = v);
            this.RaisePropertyChanged(nameof(PasswordstateUsesApiKey));
        }
    }

    public bool PasswordstateUsesApiKey => !Working.PasswordstateUseSso;

    public bool PasswordstateRequireOtp
    {
        get => Working.PasswordstateRequireOtp;
        set => Set(Working.PasswordstateRequireOtp, value, v => Working.PasswordstateRequireOtp = v);
    }

    public ProviderSecretViewModel PasswordstateApiKey { get; }

    public ProviderTestViewModel PasswordstateTest { get; }

    // ── 1Password ─────────────────────────────────────────────────────────

    public string OnePasswordCliPath
    {
        get => Working.OnePasswordCliPath;
        set => Set(Working.OnePasswordCliPath, value?.Trim() ?? string.Empty, v => Working.OnePasswordCliPath = v);
    }

    public string OnePasswordAccount
    {
        get => Working.OnePasswordAccount;
        set => Set(Working.OnePasswordAccount, value?.Trim() ?? string.Empty, v => Working.OnePasswordAccount = v);
    }

    public ProviderTestViewModel OnePasswordTest { get; }

    // ── Vault / OpenBao ───────────────────────────────────────────────────

    public string VaultUrl
    {
        get => Working.VaultUrl;
        set => Set(Working.VaultUrl, value?.Trim() ?? string.Empty, v => Working.VaultUrl = v);
    }

    public string VaultNamespace
    {
        get => Working.VaultNamespace;
        set => Set(Working.VaultNamespace, value?.Trim() ?? string.Empty, v => Working.VaultNamespace = v);
    }

    public IReadOnlyList<Choice<VaultAuthMethod>> VaultAuthMethods { get; } =
    [
        new(VaultAuthMethod.Token, "Token"),
        new(VaultAuthMethod.UserPass, Localizer.Get("VaultAuthUserPass")),
        new(VaultAuthMethod.Ldap, "LDAP"),
        new(VaultAuthMethod.AppRole, "AppRole"),
    ];

    public Choice<VaultAuthMethod> SelectedVaultAuthMethod
    {
        get => VaultAuthMethods.FirstOrDefault(c => c.Value == Working.VaultAuthMethod) ?? VaultAuthMethods[0];
        set
        {
            if (value is null) return;
            if (value.Value == Working.VaultAuthMethod) return;
            Set(Working.VaultAuthMethod, value.Value, v => Working.VaultAuthMethod = v);
            // A token is not a password: never reuse the saved secret for another method.
            VaultSecret.ForgetCommand.Execute().Subscribe();
            this.RaisePropertyChanged(nameof(VaultUsesLogin));
            this.RaisePropertyChanged(nameof(VaultUsernameLabel));
            this.RaisePropertyChanged(nameof(VaultSecretLabel));
            this.RaisePropertyChanged(nameof(VaultAuthMountWatermark));
        }
    }

    public bool VaultUsesLogin => Working.VaultAuthMethod != VaultAuthMethod.Token;

    public string VaultUsernameLabel => Working.VaultAuthMethod == VaultAuthMethod.AppRole ? Localizer.Get("RoleId") : Localizer.Get("Username");

    public string VaultSecretLabel => Working.VaultAuthMethod switch
    {
        VaultAuthMethod.Token => Localizer.Get("Token"),
        VaultAuthMethod.AppRole => Localizer.Get("SecretId"),
        _ => Localizer.Get("Password"),
    };

    public string VaultAuthMountWatermark => Working.VaultAuthMethod switch
    {
        VaultAuthMethod.UserPass => "userpass",
        VaultAuthMethod.Ldap => "ldap",
        VaultAuthMethod.AppRole => "approle",
        _ => string.Empty,
    };

    public string VaultAuthMount
    {
        get => Working.VaultAuthMount;
        set => Set(Working.VaultAuthMount, value?.Trim() ?? string.Empty, v => Working.VaultAuthMount = v);
    }

    public string VaultUsername
    {
        get => Working.VaultUsername;
        set => Set(Working.VaultUsername, value?.Trim() ?? string.Empty, v => Working.VaultUsername = v);
    }

    public string VaultCaCertificatePath
    {
        get => Working.VaultCaCertificatePath;
        set => Set(Working.VaultCaCertificatePath, value?.Trim() ?? string.Empty, v => Working.VaultCaCertificatePath = v);
    }

    public ProviderSecretViewModel VaultSecret { get; }

    public ProviderTestViewModel VaultTest { get; }

    // ── AWS EC2 ───────────────────────────────────────────────────────────

    public IReadOnlyList<Choice<AwsCredentialSource>> AwsCredentialSources { get; } =
    [
        new(AwsCredentialSource.DefaultChain, Localizer.Get("AwsDefaultCredentialChain")),
        new(AwsCredentialSource.Profile, Localizer.Get("AwsNamedProfile")),
        new(AwsCredentialSource.AccessKey, Localizer.Get("AwsSavedAccessKey")),
    ];

    public Choice<AwsCredentialSource> SelectedAwsCredentialSource
    {
        get => AwsCredentialSources.FirstOrDefault(c => c.Value == Working.AwsCredentialSource) ?? AwsCredentialSources[0];
        set
        {
            if (value is null) return;
            Set(Working.AwsCredentialSource, value.Value, v => Working.AwsCredentialSource = v);
            this.RaisePropertyChanged(nameof(AwsUsesProfile));
            this.RaisePropertyChanged(nameof(AwsUsesAccessKey));
        }
    }

    public bool AwsUsesProfile => Working.AwsCredentialSource == AwsCredentialSource.Profile;

    public bool AwsUsesAccessKey => Working.AwsCredentialSource == AwsCredentialSource.AccessKey;

    public string AwsProfile
    {
        get => Working.AwsProfile;
        set => Set(Working.AwsProfile, value?.Trim() ?? string.Empty, v => Working.AwsProfile = v);
    }

    public string AwsAccessKeyId
    {
        get => Working.AwsAccessKeyId;
        set => Set(Working.AwsAccessKeyId, value?.Trim() ?? string.Empty, v => Working.AwsAccessKeyId = v);
    }

    public ProviderSecretViewModel AwsSecretAccessKey { get; }

    public string AwsDefaultRegion
    {
        get => Working.AwsDefaultRegion;
        set => Set(Working.AwsDefaultRegion, value?.Trim() ?? string.Empty, v => Working.AwsDefaultRegion = v);
    }

    public IReadOnlyList<Choice<AwsAddressKind>> AwsAddressKinds { get; } =
    [
        new(AwsAddressKind.PublicIp, Localizer.Get("AwsPublicIp")),
        new(AwsAddressKind.PrivateIp, Localizer.Get("AwsPrivateIp")),
        new(AwsAddressKind.PublicDnsName, Localizer.Get("AwsPublicDnsName")),
        new(AwsAddressKind.PrivateDnsName, Localizer.Get("AwsPrivateDnsName")),
    ];

    public Choice<AwsAddressKind> SelectedAwsAddressKind
    {
        get => AwsAddressKinds.FirstOrDefault(c => c.Value == Working.AwsAddressKind) ?? AwsAddressKinds[0];
        set
        {
            if (value is null) return;
            Set(Working.AwsAddressKind, value.Value, v => Working.AwsAddressKind = v);
        }
    }

    public string AwsServiceUrl
    {
        get => Working.AwsServiceUrl;
        set => Set(Working.AwsServiceUrl, value?.Trim() ?? string.Empty, v => Working.AwsServiceUrl = v);
    }

    public ProviderTestViewModel AwsTest { get; }
}
