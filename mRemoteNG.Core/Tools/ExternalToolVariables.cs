using System.Globalization;
using mRemoteNG.Core.Connection;

namespace mRemoteNG.Core.Tools;

/// <summary>
/// Values of the connection variables an external tool can use (<c>%NAME%</c>, <c>%HOSTNAME%</c>, …).
/// </summary>
public sealed record ExternalToolVariables
{
    public string Name { get; init; } = string.Empty;
    public string Hostname { get; init; } = string.Empty;
    public string Port { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string MacAddress { get; init; } = string.Empty;
    public string UserField { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;

    /// <summary>The names <see cref="TryGet"/> understands (case-insensitive), as documented to users.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["NAME", "HOSTNAME", "PORT", "USERNAME", "PASSWORD", "DOMAIN", "DESCRIPTION", "MACADDRESS", "USERFIELD", "PROTOCOL"];

    /// <summary>Reads the (inherited) values of a connection-tree node.</summary>
    public static ExternalToolVariables FromConnection(ConnectionInfo connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new ExternalToolVariables
        {
            Name = connection.Name ?? string.Empty,
            Hostname = connection.Hostname ?? string.Empty,
            Port = connection.Port.ToString(CultureInfo.InvariantCulture),
            Username = connection.Username ?? string.Empty,
            Password = connection.Password ?? string.Empty,
            Domain = connection.Domain ?? string.Empty,
            Description = connection.Description ?? string.Empty,
            MacAddress = connection.MacAddress ?? string.Empty,
            UserField = connection.UserField ?? string.Empty,
            Protocol = connection.Protocol.ToString(),
        };
    }

    /// <summary>Looks up a variable by name (case-insensitive); false for names that are not connection variables.</summary>
    public bool TryGet(string name, out string value)
    {
        string? result = name.ToLowerInvariant() switch
        {
            "name" => Name,
            "hostname" => Hostname,
            "port" => Port,
            "username" => Username,
            "password" => Password,
            "domain" => Domain,
            "description" => Description,
            "macaddress" => MacAddress,
            "userfield" => UserField,
            "protocol" => Protocol,
            _ => null,
        };
        value = result ?? string.Empty;
        return result is not null;
    }
}
