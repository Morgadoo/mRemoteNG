using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using mRemoteNG.Core.Config.Putty;
using mRemoteNG.Platform;
using mRemoteNG.Protocols.Abstractions;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>DI registrations shared by the SSH shell and SFTP clients.</summary>
public static class SshServices
{
    /// <summary>
    /// Registers the known_hosts store, the host key verifier, <see cref="SftpSession"/>, the PuTTY
    /// saved-session services and the SSH connection preparation steps (PuTTY session / SSH options,
    /// SSH tunnels). The UI replaces <see cref="ISshUserPrompt"/> with an interactive implementation;
    /// without one, unknown and changed host keys are rejected.
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        services.TryAddSingleton(_ => KnownHostsStore.CreateDefault());
        services.TryAddSingleton<ISshUserPrompt, NonInteractiveSshUserPrompt>();
        services.TryAddSingleton<IHostKeyVerifier, KnownHostsHostKeyVerifier>();
        services.TryAddTransient<SftpSession>();

        // PuTTY saved sessions: the platform provider (registry on Windows) when registered, else ~/.putty/sessions.
        services.TryAddSingleton(sp => new PuttySessionCatalog(sp.GetService<IPuttySessionsProvider>()));
        services.TryAddSingleton(sp => new PuttySessionsTree(sp.GetRequiredService<PuttySessionCatalog>()));

        services.AddSingleton<IConnectionPreparationStep>(sp => new SshSettingsPreparationStep(
            sp.GetRequiredService<PuttySessionCatalog>(),
            sp.GetService<ILogger<SshSettingsPreparationStep>>()));
        services.AddSingleton<IConnectionPreparationStep>(sp => new SshTunnelPreparationStep(
            sp.GetRequiredService<IHostKeyVerifier>(),
            sp.GetRequiredService<ISshUserPrompt>(),
            sp,
            sp.GetService<ILogger<SshTunnelPreparationStep>>()));
    }
}
