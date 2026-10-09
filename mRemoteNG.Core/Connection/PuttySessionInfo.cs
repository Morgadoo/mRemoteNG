using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Tree;
using mRemoteNG.Platform;

namespace mRemoteNG.Core.Connection
{
    /// <summary>
    /// A PuTTY saved session shown under the "PuTTY Sessions" root. It is read-only (it belongs to PuTTY,
    /// not to the connection file) but can be connected to or copied into the connection tree.
    /// </summary>
    public sealed class PuttySessionInfo : ConnectionInfo
    {
        public PuttySessionInfo(PuttySession session)
            : base("putty:" + (session ?? throw new ArgumentNullException(nameof(session))).Name)
        {
            ConnectionDefaults.ApplyNewConnectionDefaults(this);
            Name = session.Name;
            PuttySession = session.Name;
            Protocol = MapProtocol(session.Protocol);
            Hostname = session.Hostname;
            Port = session.Port > 0 ? session.Port : GetDefaultPort(Protocol);
            Username = session.Username;
            Icon = "PuTTY";
        }

        public override TreeNodeType GetTreeNodeType() => TreeNodeType.PuttySession;

        /// <summary>A regular connection with the session's settings, for adding to the connection tree.</summary>
        public ConnectionInfo ToConnection()
        {
            var connection = ConnectionDefaults.ApplyNewConnectionDefaults(new ConnectionInfo());
            connection.Name = Name;
            connection.Protocol = Protocol;
            connection.Hostname = Hostname;
            connection.Port = Port;
            connection.Username = Username;
            connection.PuttySession = PuttySession;
            return connection;
        }

        /// <summary>Maps PuTTY's protocol names ("ssh", "telnet", "rlogin", "raw") to mRemoteNG protocols.</summary>
        public static ProtocolType MapProtocol(string? puttyProtocol) => puttyProtocol?.Trim().ToLowerInvariant() switch
        {
            "telnet" => ProtocolType.Telnet,
            "rlogin" => ProtocolType.Rlogin,
            "raw" => ProtocolType.RAW,
            _ => ProtocolType.SSH2,
        };
    }
}
