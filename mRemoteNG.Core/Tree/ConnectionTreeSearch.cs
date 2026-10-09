using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Tree
{
    /// <summary>Result of filtering a connection tree with <see cref="ConnectionTreeSearch.Filter"/>.</summary>
    public sealed class ConnectionTreeSearchResult
    {
        internal ConnectionTreeSearchResult(HashSet<ConnectionInfo> visible, HashSet<ContainerInfo> expand, HashSet<ConnectionInfo> matches)
        {
            Visible = visible;
            ContainersToExpand = expand;
            Matches = matches;
        }

        /// <summary>Nodes that stay visible: matches, their ancestors and the contents of matching folders.</summary>
        public IReadOnlySet<ConnectionInfo> Visible { get; }

        /// <summary>Containers that must be expanded so every match can be seen.</summary>
        public IReadOnlySet<ContainerInfo> ContainersToExpand { get; }

        /// <summary>Nodes that matched the filter themselves.</summary>
        public IReadOnlySet<ConnectionInfo> Matches { get; }
    }

    /// <summary>Case-insensitive search over name, hostname and description of a connection tree.</summary>
    public static class ConnectionTreeSearch
    {
        public static bool Matches(ConnectionInfo node, string filter)
        {
            ArgumentNullException.ThrowIfNull(node);
            if (string.IsNullOrWhiteSpace(filter))
                return true;

            var term = filter.Trim();
            return Contains(node.Name, term)
                || Contains(node.Hostname, term)
                || Contains(node.Description, term);
        }

        /// <summary>
        /// Computes which nodes stay visible for <paramref name="filter"/>. The root is always visible.
        /// A blank filter leaves every node visible and expands nothing.
        /// </summary>
        public static ConnectionTreeSearchResult Filter(ContainerInfo root, string filter)
        {
            ArgumentNullException.ThrowIfNull(root);

            var visible = new HashSet<ConnectionInfo> { root };
            var expand = new HashSet<ContainerInfo>();
            var matches = new HashSet<ConnectionInfo>();

            if (string.IsNullOrWhiteSpace(filter))
            {
                foreach (var node in root.GetRecursiveChildList())
                    visible.Add(node);
                return new ConnectionTreeSearchResult(visible, expand, matches);
            }

            foreach (var node in root.GetRecursiveChildList())
            {
                if (!Matches(node, filter))
                    continue;

                matches.Add(node);
                visible.Add(node);

                // Keep every ancestor visible and expanded so the match can be seen.
                for (var parent = node.Parent; parent is not null; parent = parent.Parent)
                {
                    visible.Add(parent);
                    expand.Add(parent);
                    if (ReferenceEquals(parent, root))
                        break;
                }

                // A matching folder shows its whole content.
                if (node is ContainerInfo container)
                {
                    foreach (var descendant in container.GetRecursiveChildList())
                        visible.Add(descendant);
                }
            }

            return new ConnectionTreeSearchResult(visible, expand, matches);
        }

        private static bool Contains(string? value, string term) =>
            !string.IsNullOrEmpty(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
