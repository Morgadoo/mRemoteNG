using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using mRemoteNG.Avalonia.ViewModels.Docking;
using mRemoteNG.Core.Settings;
using mRemoteNG.Core.Tools;
using mRemoteNG.Protocols.Abstractions;
using mRemoteNG.Protocols.External;
using LogLevel = mRemoteNG.Avalonia.ViewModels.Docking.LogLevel;

namespace mRemoteNG.Avalonia.Services;

/// <summary>
/// Connects <see cref="ExternalToolsService"/> to the application: its messages go to the log panel, integrated
/// tools open session tabs in the sessions dock, and an empty user name falls back to the default user name from
/// the options (as the legacy "empty credentials" option did).
/// </summary>
public static class ExternalToolsIntegration
{
    public static ExternalToolsService Create(IServiceProvider services)
    {
        var tools = new ExternalToolsService(
            services.GetRequiredService<ExternalToolsRepository>(),
            services.GetRequiredService<ExternalToolLauncher>(),
            services.GetService<ILogger<ExternalToolsService>>());

        var log = services.GetService<LogPanelDockable>();
        if (log is not null)
            tools.Message += (_, e) => log.Log(e.Message, ToLogLevel(e.Level));

        var settings = services.GetService<AppSettingsService>();
        if (settings is not null)
            tools.VariablesFilter = variables => ApplyDefaultUsername(variables, settings.Current.DefaultUsername);

        // Resolved on use: the sessions dock depends (through the connection preparer) on this service.
        tools.OpenIntegratedSession = async connection =>
        {
            var sessions = services.GetRequiredService<SessionsDockable>();
            await sessions.OpenConnectionAsync(connection, services.GetRequiredService<IProtocolFactory>());
        };
        return tools;
    }

    public static ExternalToolVariables ApplyDefaultUsername(ExternalToolVariables variables, string? defaultUsername) =>
        string.IsNullOrEmpty(variables.Username) && !string.IsNullOrEmpty(defaultUsername)
            ? variables with { Username = defaultUsername }
            : variables;

    public static LogLevel ToLogLevel(ExternalToolMessageLevel level) => level switch
    {
        ExternalToolMessageLevel.Error => LogLevel.Error,
        ExternalToolMessageLevel.Warning => LogLevel.Warning,
        _ => LogLevel.Info,
    };
}
