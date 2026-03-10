using mRemoteNG.Core.Connection;

namespace mRemoteNG.Core.Tree
{
    public class NodeSearcher
    {
        private readonly ConnectionTreeModel _connectionTreeModel;

        public NodeSearcher(ConnectionTreeModel connectionTreeModel)
        {
            _connectionTreeModel = connectionTreeModel ?? throw new ArgumentNullException(nameof(connectionTreeModel));
        }

        public IEnumerable<ConnectionInfo> SearchByName(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return Enumerable.Empty<ConnectionInfo>();

            return _connectionTreeModel.GetRecursiveChildList()
                .Where(c => c.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        }

        public IEnumerable<ConnectionInfo> SearchByHostname(string hostname)
        {
            if (string.IsNullOrWhiteSpace(hostname))
                return Enumerable.Empty<ConnectionInfo>();

            return _connectionTreeModel.GetRecursiveChildList()
                .Where(c => c.Hostname.Contains(hostname, StringComparison.OrdinalIgnoreCase));
        }
    }
}
