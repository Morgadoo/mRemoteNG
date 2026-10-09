using mRemoteNG.Core.Tree;

namespace mRemoteNG.Core.Config.Connections
{
    public interface IConnectionsLoader
    {
        ConnectionTreeModel Load();
    }
}
