using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>
    /// Gives imported nodes new IDs where theirs are already used, so importing a file twice (or a
    /// file exported from the same tree) never produces two nodes with the same ID.
    /// </summary>
    public static class ConnectionIdRemapper
    {
        /// <summary>
        /// Replaces every node below <paramref name="importRoot"/> whose ID is in <paramref name="usedIds"/>,
        /// or repeats an earlier imported ID, with an identical node that has a new ID.
        /// The IDs of all imported nodes are added to <paramref name="usedIds"/>.
        /// </summary>
        /// <returns>The number of nodes that got a new ID.</returns>
        public static int EnsureUniqueIds(ContainerInfo importRoot, ISet<string> usedIds)
        {
            ArgumentNullException.ThrowIfNull(importRoot);
            ArgumentNullException.ThrowIfNull(usedIds);
            return Remap(importRoot, usedIds);
        }

        /// <summary>The IDs of <paramref name="node"/> and everything below it.</summary>
        public static HashSet<string> CollectIds(ConnectionInfo node)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node.ConstantID };
            if (node is ContainerInfo container)
            {
                foreach (var child in container.GetRecursiveChildList())
                    ids.Add(child.ConstantID);
            }
            return ids;
        }

        private static int Remap(ContainerInfo container, ISet<string> usedIds)
        {
            var count = 0;
            foreach (var child in container.Children.ToArray())
            {
                var current = child;
                if (!usedIds.Add(child.ConstantID))
                {
                    current = Reidentify(child, container);
                    usedIds.Add(current.ConstantID);
                    count++;
                }

                if (current is ContainerInfo childContainer)
                    count += Remap(childContainer, usedIds);
            }
            return count;
        }

        private static ConnectionInfo Reidentify(ConnectionInfo node, ContainerInfo parent)
        {
            var index = parent.Children.IndexOf(node);

            // Detach first so CopyFrom copies the node's own values rather than inherited ones.
            parent.RemoveChild(node);

            var newId = Guid.NewGuid().ToString();
            ConnectionInfo copy = node is ContainerInfo ? new ContainerInfo(newId) : new ConnectionInfo(newId);
            copy.CopyFrom(node);

            if (node is ContainerInfo oldContainer && copy is ContainerInfo newContainer)
            {
                newContainer.IsExpanded = oldContainer.IsExpanded;
                foreach (var grandChild in oldContainer.Children.ToArray())
                    newContainer.AddChild(grandChild);
            }

            parent.AddChildAt(copy, index);
            return copy;
        }
    }
}
