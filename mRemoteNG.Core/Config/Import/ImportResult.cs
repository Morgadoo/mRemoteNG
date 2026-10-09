using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;

namespace mRemoteNG.Core.Config.Import
{
    /// <summary>What an import added to the tree.</summary>
    public sealed class ImportResult
    {
        public ImportResult(IReadOnlyList<ConnectionInfo> importedNodes, IReadOnlyList<string>? warnings = null, int reassignedIdCount = 0)
        {
            ImportedNodes = importedNodes ?? throw new ArgumentNullException(nameof(importedNodes));
            Warnings = warnings ?? [];
            ReassignedIdCount = reassignedIdCount;

            foreach (var node in importedNodes)
            {
                Count(node);
                if (node is ContainerInfo container)
                {
                    foreach (var child in container.GetRecursiveChildList())
                        Count(child);
                }
            }
        }

        /// <summary>The nodes added directly to the destination folder (each may hold a subtree).</summary>
        public IReadOnlyList<ConnectionInfo> ImportedNodes { get; }

        public IReadOnlyList<string> Warnings { get; }

        /// <summary>Imported nodes whose ID already existed in the tree and were given a new one.</summary>
        public int ReassignedIdCount { get; }

        /// <summary>Connections imported, in all imported folders.</summary>
        public int ConnectionCount { get; private set; }

        /// <summary>Folders imported, including a folder created to hold the imported file.</summary>
        public int FolderCount { get; private set; }

        public string Summary
        {
            get
            {
                var text = $"{ConnectionCount} connection{(ConnectionCount == 1 ? "" : "s")} in {FolderCount} folder{(FolderCount == 1 ? "" : "s")}";
                if (ReassignedIdCount > 0)
                    text += $", {ReassignedIdCount} duplicate ID{(ReassignedIdCount == 1 ? "" : "s")} replaced";
                return text;
            }
        }

        private void Count(ConnectionInfo node)
        {
            if (node is ContainerInfo)
                FolderCount++;
            else
                ConnectionCount++;
        }
    }
}
