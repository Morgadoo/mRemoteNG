using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Connection.Protocol;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;
using mRemoteNG.Platform;

namespace mRemoteNG.Core.Config.Putty
{
    /// <summary>
    /// A connection-tree node for a PuTTY saved session. Its address, user and protocol come from the
    /// session, and <see cref="ConnectionInfo.PuttySession"/> names it, so connecting applies the rest
    /// of the session's settings (key, forwardings, proxy…).
    /// </summary>
    public sealed class PuttySessionNodeInfo : ConnectionInfo
    {
        public PuttySessionNodeInfo(string sessionName)
            : base("putty-session:" + sessionName)
        {
            Name = sessionName;
            PuttySession = sessionName;
            Icon = "PuTTY";
        }

        /// <summary>The session's settings as last read.</summary>
        public PuttySessionSettings? Settings { get; internal set; }

        public override TreeNodeType GetTreeNodeType() => TreeNodeType.PuttySession;

        /// <summary>A copy as an ordinary connection (for "copy to my connections").</summary>
        public override ConnectionInfo Clone()
        {
            var copy = new ConnectionInfo();
            copy.CopyFrom(this);
            return copy;
        }
    }

    /// <summary>
    /// Maintains the "PuTTY Sessions" root of the connection tree: one <see cref="PuttySessionNodeInfo"/>
    /// per saved session, kept in sync with PuTTY by <see cref="RefreshAsync"/> (sessions added, removed
    /// or changed in PuTTY appear, disappear or update; existing nodes keep their identity).
    /// </summary>
    public sealed class PuttySessionsTree
    {
        private readonly PuttySessionCatalog _catalog;

        public PuttySessionsTree(PuttySessionCatalog catalog)
        {
            _catalog = catalog;
        }

        public RootPuttySessionsNodeInfo Root { get; } = new();

        /// <summary>Raised after a refresh that changed the root's children.</summary>
        public event EventHandler? Changed;

        /// <summary>
        /// Re-reads the saved sessions and updates <see cref="Root"/>. The sessions are read on a worker
        /// thread; the tree is changed on the caller's synchronization context (call it from the UI thread
        /// when the root is bound to the UI). Returns true when anything changed.
        /// </summary>
        public async Task<bool> RefreshAsync()
        {
            var sessions = await Task.Run(_catalog.GetSessionsAsync).ConfigureAwait(true);
            var changed = Apply(sessions);
            if (changed)
                Changed?.Invoke(this, EventArgs.Empty);
            return changed;
        }

        /// <summary>Updates <see cref="Root"/> to show <paramref name="sessions"/>; returns true when anything changed.</summary>
        public bool Apply(IReadOnlyList<PuttySession> sessions)
        {
            var wanted = new Dictionary<string, PuttySession>(StringComparer.Ordinal);
            foreach (var session in sessions)
            {
                // "Default Settings" only holds defaults; it is listed only if it names a host.
                if (PuttySessionSettings.IsDefaultSession(session.Name) && string.IsNullOrWhiteSpace(session.Hostname))
                    continue;
                if (MapProtocol(session) is null)
                    continue;
                wanted.TryAdd(session.Name, session);
            }

            var changed = false;
            foreach (var node in Root.Children.OfType<PuttySessionNodeInfo>().ToList())
            {
                if (!wanted.ContainsKey(node.PuttySession))
                {
                    Root.RemoveChild(node);
                    changed = true;
                }
            }

            foreach (var session in wanted.Values)
            {
                var node = Root.Children.OfType<PuttySessionNodeInfo>().FirstOrDefault(n => n.PuttySession == session.Name);
                if (node is null)
                {
                    node = new PuttySessionNodeInfo(session.Name);
                    Update(node, session);
                    Root.AddChild(node);
                    changed = true;
                }
                else
                {
                    changed |= Update(node, session);
                }
            }

            if (changed)
                Root.Sort();
            return changed;
        }

        /// <summary>Creates a stand-alone node for one session; null when its protocol is not supported.</summary>
        public static PuttySessionNodeInfo? CreateNode(PuttySession session)
        {
            if (MapProtocol(session) is null)
                return null;
            var node = new PuttySessionNodeInfo(session.Name);
            Update(node, session);
            return node;
        }

        private static bool Update(PuttySessionNodeInfo node, PuttySession session)
        {
            var settings = PuttySessionSettings.FromSession(session);
            var protocol = MapProtocol(session)!.Value;
            var port = settings.PortNumber > 0 ? settings.PortNumber : ConnectionInfo.GetDefaultPort(protocol);

            var changed = node.Hostname != settings.HostName
                          || node.Port != port
                          || node.Username != settings.UserName
                          || node.Protocol != protocol;
            node.Protocol = protocol;
            node.Hostname = settings.HostName;
            node.Port = port;
            node.Username = settings.UserName;
            node.Settings = settings;
            return changed;
        }

        /// <summary>Maps PuTTY's protocol name; null for protocols mRemoteNG does not open (serial, supdup…).</summary>
        public static ProtocolType? MapProtocol(PuttySession session) => MapProtocol(session.Protocol);

        /// <inheritdoc cref="MapProtocol(PuttySession)"/>
        public static ProtocolType? MapProtocol(string? puttyProtocol)
        {
            var protocol = string.IsNullOrWhiteSpace(puttyProtocol) ? "ssh" : puttyProtocol.Trim().ToLowerInvariant();
            return protocol switch
            {
                "ssh" => ProtocolType.SSH2,
                "telnet" => ProtocolType.Telnet,
                "rlogin" => ProtocolType.Rlogin,
                "raw" => ProtocolType.RAW,
                _ => null,
            };
        }
    }
}
