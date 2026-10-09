using mRemoteNG.Core.Tree;

namespace mRemoteNG.Core.Config.Connections
{
    public interface IConnectionsSaver
    {
        void Save(ConnectionTreeModel connectionTreeModel);
    }
}
