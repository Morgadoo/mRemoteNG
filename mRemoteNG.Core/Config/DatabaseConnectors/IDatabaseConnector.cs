using System.Data.Common;

namespace mRemoteNG.Core.Config.DatabaseConnectors
{
    /// <summary>An open-able connection to the SQL server that stores the connection tree.</summary>
    public interface IDatabaseConnector : IDisposable
    {
        DatabaseServerType ServerType { get; }

        /// <summary>The SQL flavour (DDL, quoting, metadata queries) of this server.</summary>
        SqlDialect Dialect { get; }

        bool IsConnected { get; }

        /// <summary>The underlying ADO.NET connection.</summary>
        DbConnection Connection { get; }

        void Connect();

        Task ConnectAsync(CancellationToken cancellationToken = default);

        void Disconnect();

        /// <summary>Creates a command on this connection (enlisted in <paramref name="transaction"/> when given).</summary>
        DbCommand CreateCommand(string sql, DbTransaction? transaction = null);
    }
}
