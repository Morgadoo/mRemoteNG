using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Tree
{
    /// <summary>Structural edits on the connection tree (duplicate, reorder, move).</summary>
    public static class ConnectionTreeOperations
    {
        public const string CopySuffix = " (copy)";

        /// <summary>
        /// Creates a deep copy of <paramref name="node"/> with new IDs and a " (copy)" name suffix,
        /// and inserts it directly below the original in the same folder.
        /// </summary>
        /// <exception cref="InvalidOperationException">The node is the root or is not in a tree.</exception>
        public static ConnectionInfo Duplicate(ConnectionInfo node)
        {
            ArgumentNullException.ThrowIfNull(node);
            if (node is RootNodeInfo)
                throw new InvalidOperationException("The root node cannot be duplicated.");
            var parent = node.Parent
                ?? throw new InvalidOperationException("Only nodes inside a tree can be duplicated.");

            var copy = node.Clone();
            copy.RemoveParent();
            copy.Name = node.Name + CopySuffix;
            parent.AddChildBelow(copy, node);
            return copy;
        }

        public static bool CanMoveUp(ConnectionInfo node) =>
            node.Parent is { } parent && parent.Children.IndexOf(node) > 0;

        public static bool CanMoveDown(ConnectionInfo node) =>
            node.Parent is { } parent && parent.Children.IndexOf(node) is var i && i >= 0 && i < parent.Children.Count - 1;

        /// <summary>Moves the node one position up among its siblings. Returns false when it is already first.</summary>
        public static bool MoveUp(ConnectionInfo node)
        {
            if (!CanMoveUp(node)) return false;
            var parent = node.Parent!;
            parent.SetChildPosition(node, parent.Children.IndexOf(node) - 1);
            return true;
        }

        /// <summary>Moves the node one position down among its siblings. Returns false when it is already last.</summary>
        public static bool MoveDown(ConnectionInfo node)
        {
            if (!CanMoveDown(node)) return false;
            var parent = node.Parent!;
            parent.SetChildPosition(node, parent.Children.IndexOf(node) + 1);
            return true;
        }

        /// <summary>
        /// True when <paramref name="node"/> may be moved into <paramref name="target"/>: the root never moves,
        /// and a folder cannot be moved into itself or one of its descendants.
        /// </summary>
        public static bool CanMoveInto(ConnectionInfo node, ContainerInfo target)
        {
            ArgumentNullException.ThrowIfNull(node);
            ArgumentNullException.ThrowIfNull(target);
            if (node is RootNodeInfo || node.Parent is null)
                return false;

            for (ConnectionInfo? current = target; current is not null; current = current.Parent)
            {
                if (ReferenceEquals(current, node))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Moves <paramref name="node"/> into <paramref name="target"/> at <paramref name="index"/>
        /// (appended when negative or past the end).
        /// </summary>
        /// <exception cref="InvalidOperationException">The move is not allowed (see <see cref="CanMoveInto"/>).</exception>
        public static void MoveInto(ConnectionInfo node, ContainerInfo target, int index = -1)
        {
            if (!CanMoveInto(node, target))
                throw new InvalidOperationException($"Cannot move \"{node.Name}\" into \"{target.Name}\".");

            if (ReferenceEquals(node.Parent, target))
            {
                var current = target.Children.IndexOf(node);
                var newIndex = index < 0 || index >= target.Children.Count ? target.Children.Count - 1 : index;
                // Indices given relative to the list before removal shift by one when moving down.
                if (index >= 0 && newIndex > current) newIndex--;
                target.SetChildPosition(node, Math.Max(0, newIndex));
                return;
            }

            var insertAt = index < 0 || index > target.Children.Count ? target.Children.Count : index;
            target.AddChildAt(node, insertAt);
        }

        /// <summary>Moves <paramref name="node"/> into the folder of <paramref name="reference"/>, directly above it.</summary>
        public static void MoveAbove(ConnectionInfo node, ConnectionInfo reference)
        {
            var target = reference.Parent
                ?? throw new InvalidOperationException("The reference node is not in a tree.");
            if (ReferenceEquals(node, reference)) return;
            MoveInto(node, target, target.Children.IndexOf(reference));
        }

        /// <summary>Number of folders and connections below <paramref name="container"/> (recursive).</summary>
        public static int CountDescendants(ContainerInfo container) =>
            container.GetRecursiveChildList().Count();
    }
}
