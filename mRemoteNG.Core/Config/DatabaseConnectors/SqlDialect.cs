using System.Text;
using mRemoteNG.Core.Config.Serializers.Sql;

namespace mRemoteNG.Core.Config.DatabaseConnectors
{
    /// <summary>
    /// SQL that differs between SQL Server and MySQL/MariaDB: identifier quoting, column types and the
    /// DDL for the legacy connection tables (<see cref="SqlSchema"/>).
    /// New databases use Unicode text columns (nvarchar / utf8mb3) so names outside Latin-1 survive;
    /// the legacy app reads them the same way as its own varchar/latin1 tables.
    /// </summary>
    public abstract class SqlDialect
    {
        public static SqlDialect MySql { get; } = new MySqlDialect();

        public static SqlDialect MsSql { get; } = new MsSqlDialect();

        public static SqlDialect For(DatabaseServerType serverType) =>
            serverType == DatabaseServerType.MySql ? MySql : MsSql;

        public abstract DatabaseServerType ServerType { get; }

        /// <summary>Most parameters one command may carry (SQL Server allows 2100).</summary>
        public abstract int MaxParametersPerCommand { get; }

        /// <summary>Counts the tables named @name in the current database/schema.</summary>
        public abstract string TableExistsSql { get; }

        public abstract string Quote(string identifier);

        /// <summary>The column's SQL type, e.g. "varchar(128)", "bit", "tinyint".</summary>
        public abstract string ColumnType(SqlColumn column);

        protected abstract string IdentityDefinition { get; }

        protected abstract string UpdateTimestampType { get; }

        protected abstract string BoolType { get; }

        protected abstract string TableSuffix { get; }

        /// <summary>CREATE TABLE statement for one of <see cref="SqlSchema.Tables"/>.</summary>
        public string CreateTableSql(string table) => table switch
        {
            SqlSchema.ConnectionsTable => CreateConnectionsTableSql(),
            SqlSchema.RootTable =>
                $"CREATE TABLE {Quote(SqlSchema.RootTable)} (\n" +
                $"    {Quote("Name")} {StringType(2048)} NOT NULL,\n" +
                $"    {Quote("Export")} {BoolType} NOT NULL,\n" +
                $"    {Quote("Protected")} {StringType(4048)} NOT NULL,\n" +
                $"    {Quote("ConfVersion")} {StringType(15)} NOT NULL\n" +
                $"){TableSuffix}",
            SqlSchema.UpdateTable =>
                $"CREATE TABLE {Quote(SqlSchema.UpdateTable)} (\n" +
                $"    {Quote("LastUpdate")} {UpdateTimestampType} NULL\n" +
                $"){TableSuffix}",
            _ => throw new ArgumentException($"Unknown table {table}.", nameof(table)),
        };

        public string CreateConnectionsTableSql()
        {
            var sql = new StringBuilder();
            sql.Append("CREATE TABLE ").Append(Quote(SqlSchema.ConnectionsTable)).Append(" (\n");
            foreach (var column in SqlSchema.ConnectionColumns)
                sql.Append("    ").Append(ColumnDefinition(column, forAlter: false)).Append(",\n");
            sql.Append(ConnectionsTableConstraints());
            sql.Append('\n').Append(')').Append(TableSuffix);
            return sql.ToString();
        }

        /// <summary>
        /// ALTER TABLE statement adding a missing column (schema upgrades). NOT NULL columns get a default so
        /// the statement also works on a table that already has rows.
        /// </summary>
        public string AddColumnSql(SqlColumn column) =>
            $"ALTER TABLE {Quote(SqlSchema.ConnectionsTable)} ADD {ColumnDefinition(column, forAlter: true)}";

        /// <summary>"name type [NOT] NULL [DEFAULT x]".</summary>
        public string ColumnDefinition(SqlColumn column, bool forAlter)
        {
            if (column.Kind == SqlColumnKind.Identity)
                return $"{Quote(column.Name)} {IdentityDefinition}";

            var definition = new StringBuilder();
            definition.Append(Quote(column.Name)).Append(' ').Append(ColumnType(column));
            definition.Append(column.Nullable ? " NULL" : " NOT NULL");

            var defaultValue = column.DefaultValue;
            if (defaultValue is null && forAlter && !column.Nullable)
            {
                defaultValue = column.Kind switch
                {
                    SqlColumnKind.String => "''",
                    SqlColumnKind.DateTime => "CURRENT_TIMESTAMP",
                    _ => "0",
                };
            }

            if (defaultValue is not null)
                definition.Append(" DEFAULT ").Append(defaultValue);
            return definition.ToString();
        }

        protected abstract string ConnectionsTableConstraints();

        protected abstract string StringType(int size);

        private sealed class MySqlDialect : SqlDialect
        {
            public override DatabaseServerType ServerType => DatabaseServerType.MySql;

            public override int MaxParametersPerCommand => 30000;

            public override string TableExistsSql =>
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @name";

            protected override string IdentityDefinition => "int NOT NULL AUTO_INCREMENT";

            protected override string UpdateTimestampType => "datetime(3)";

            protected override string BoolType => "tinyint";

            // utf8mb3, not utf8mb4: the legacy varchar sizes add up to more than MySQL's 65535-byte row limit at
            // four bytes per character. utf8mb3 covers the Basic Multilingual Plane (legacy used latin1).
            protected override string TableSuffix => " ENGINE=InnoDB DEFAULT CHARSET=utf8mb3";

            public override string Quote(string identifier) => $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";

            public override string ColumnType(SqlColumn column) => column.Kind switch
            {
                SqlColumnKind.Identity => "int",
                SqlColumnKind.Bool or SqlColumnKind.TinyInt => "tinyint",
                SqlColumnKind.Int => "int",
                SqlColumnKind.String => StringType(column.Size),
                SqlColumnKind.DateTime => "datetime",
                _ => throw new ArgumentOutOfRangeException(nameof(column)),
            };

            protected override string StringType(int size) => $"varchar({size})";

            protected override string ConnectionsTableConstraints() =>
                $"    PRIMARY KEY ({Quote("ConstantID")}),\n" +
                $"    UNIQUE KEY {Quote("ID_UNIQUE")} ({Quote("ID")})";
        }

        private sealed class MsSqlDialect : SqlDialect
        {
            public override DatabaseServerType ServerType => DatabaseServerType.MsSql;

            public override int MaxParametersPerCommand => 2000;

            public override string TableExistsSql =>
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = @name AND TABLE_SCHEMA = SCHEMA_NAME()";

            protected override string IdentityDefinition => "int NOT NULL IDENTITY(1,1)";

            protected override string UpdateTimestampType => "datetime";

            protected override string BoolType => "bit";

            protected override string TableSuffix => " ON [PRIMARY]";

            public override string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

            public override string ColumnType(SqlColumn column) => column.Kind switch
            {
                SqlColumnKind.Identity => "int",
                SqlColumnKind.Bool => "bit",
                SqlColumnKind.TinyInt => "tinyint",
                SqlColumnKind.Int => "int",
                SqlColumnKind.String => StringType(column.Size),
                SqlColumnKind.DateTime => "datetime",
                _ => throw new ArgumentOutOfRangeException(nameof(column)),
            };

            protected override string StringType(int size) => size > 4000 ? "nvarchar(max)" : $"nvarchar({size})";

            protected override string ConnectionsTableConstraints() =>
                $"    CONSTRAINT {Quote("PK_tblCons")} PRIMARY KEY ({Quote("ConstantID")})";
        }
    }
}
