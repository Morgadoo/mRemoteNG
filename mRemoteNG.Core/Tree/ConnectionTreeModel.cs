using System.Collections.ObjectModel;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Tree
{
    public class ConnectionTreeModel
    {
        public RootNodeInfo RootNode { get; }

        public ConnectionTreeModel(RootNodeInfo rootNode)
        {
            RootNode = rootNode ?? throw new ArgumentNullException(nameof(rootNode));
        }

        public IEnumerable<ConnectionInfo> GetRecursiveChildList()
        {
            return RootNode.GetRecursiveChildList();
        }

        public ConnectionInfo? GetById(string id)
        {
            return GetRecursiveChildList()
                .FirstOrDefault(c => c.ConstantID == id);
        }

        public ReadOnlyCollection<ContainerInfo> GetContainers()
        {
            return GetRecursiveChildList()
                .OfType<ContainerInfo>()
                .ToList()
                .AsReadOnly();
        }

        public ReadOnlyCollection<ConnectionInfo> GetConnections()
        {
            return GetRecursiveChildList()
                .Where(c => c is not ContainerInfo)
                .ToList()
                .AsReadOnly();
        }
    }
}
