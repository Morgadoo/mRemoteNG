using System.Data;

namespace mRemoteNG.Core.Config.DatabaseConnectors
{
    public interface IDatabaseConnector : IDisposable
    {
        bool IsConnected { get; }
        void Connect();
        void Disconnect();
        IDbCommand CreateCommand();
        IDbConnection GetConnection();
    }
}
