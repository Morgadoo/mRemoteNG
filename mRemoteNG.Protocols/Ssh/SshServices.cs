using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace mRemoteNG.Protocols.Ssh;

/// <summary>DI registrations shared by the SSH shell and SFTP clients.</summary>
public static class SshServices
{
    /// <summary>
    /// Registers the known_hosts store, the host key verifier and <see cref="SftpSession"/>.
    /// The UI replaces <see cref="ISshUserPrompt"/> with an interactive implementation; without one,
    /// unknown and changed host keys are rejected.
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        services.TryAddSingleton(_ => KnownHostsStore.CreateDefault());
        services.TryAddSingleton<ISshUserPrompt, NonInteractiveSshUserPrompt>();
        services.TryAddSingleton<IHostKeyVerifier, KnownHostsHostKeyVerifier>();
        services.TryAddTransient<SftpSession>();
    }
}
