using System.Globalization;
using System.Reflection;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Security;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Config.Serializers.Sql
{
    /// <summary>
    /// Converts between the connection tree and <c>tblCons</c> rows, following the legacy
    /// <c>DataTableSerializer</c>/<c>DataTableDeserializer</c>:
    /// <list type="bullet">
    ///   <item>One row per connection or folder, in tree (pre-order) order; <c>PositionID</c> counts from 1.</item>
    ///   <item><c>ParentID</c> is the parent folder's id, or "0" under the root (unknown parents also go to the root).</item>
    ///   <item>Enums are stored by name, booleans as bit/tinyint, passwords encrypted with the root password.</item>
    ///   <item>Each node's own values are stored (not values it inherits), plus one Inherit* flag per property.</item>
    /// </list>
    /// Missing or NULL columns keep their defaults when reading, so older and newer schemas load.
    /// </summary>
    public sealed class SqlConnectionRowMapper
    {
        public const string RootParentId = "0";

        private static readonly string[] PasswordColumns = ["Password", "RDGatewayPassword", "VNCProxyPassword"];

        /// <summary>Columns whose ConnectionInfo property has a different name.</summary>
        private static readonly Dictionary<string, string> PropertyAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectToConsole"] = nameof(ConnectionInfo.UseConsoleSession),
            ["StartProgram"] = nameof(ConnectionInfo.RDPStartProgram),
            ["StartProgramWorkDir"] = nameof(ConnectionInfo.RDPStartProgramWorkDir),
            ["EnhancedMode"] = nameof(ConnectionInfo.UseEnhancedMode),
        };

        /// <summary>Inherit* columns whose flag has a different name.</summary>
        private static readonly Dictionary<string, string> InheritAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["InheritEnhancedMode"] = nameof(ConnectionInfoInheritance.UseEnhancedMode),
            ["InheritStartProgram"] = nameof(ConnectionInfoInheritance.RDPStartProgram),
            ["InheritStartProgramWorkDir"] = nameof(ConnectionInfoInheritance.RDPStartProgramWorkDir),
        };

        private static readonly HashSet<string> StructuralColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "ID", "ConstantID", "PositionID", "ParentID", "LastChange", "Type", "Expanded", "Connected",
            "ICAEncryptionStrength", "InheritICAEncryptionStrength", "Name",
        };

        private static readonly Dictionary<string, PropertyInfo> ValueProperties = BuildValueProperties();
        private static readonly Dictionary<string, PropertyInfo> InheritProperties = BuildInheritProperties();

        private readonly ICryptographyProvider _crypto;
        private readonly SaveFilter _saveFilter;
        private readonly Func<DateTime> _clock;

        public SqlConnectionRowMapper(ICryptographyProvider crypto, SaveFilter? saveFilter = null, Func<DateTime>? clock = null)
        {
            _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
            _saveFilter = saveFilter ?? new SaveFilter();
            _clock = clock ?? (() => DateTime.Now);
        }

        /// <summary>The tblCons columns this mapper reads and writes (everything except the identity column).</summary>
        public static IEnumerable<string> MappedColumns =>
            SqlSchema.ConnectionColumns.Where(c => c.Kind != SqlColumnKind.Identity).Select(c => c.Name);

        // ── Tree → rows ───────────────────────────────────────────────────────

        /// <summary>One row per node below the root, in tree order. <paramref name="key"/> encrypts the passwords.</summary>
        public IReadOnlyList<Dictionary<string, object>> ToRows(ConnectionTreeModel model, string key)
        {
            ArgumentNullException.ThrowIfNull(model);
            var rows = new List<Dictionary<string, object>>();
            var position = 0;
            var now = TruncateToSeconds(_clock());

            void Walk(ContainerInfo container)
            {
                foreach (var child in container.Children)
                {
                    rows.Add(ToRow(child, ++position, container is RootNodeInfo ? RootParentId : container.ConstantID, key, now));
                    if (child is ContainerInfo folder)
                        Walk(folder);
                }
            }

            Walk(model.RootNode);
            return rows;
        }

        private Dictionary<string, object> ToRow(ConnectionInfo node, int position, string parentId, string key, DateTime now)
        {
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["ConstantID"] = node.ConstantID,
                ["PositionID"] = position,
                ["ParentID"] = parentId,
                ["LastChange"] = now,
                ["Name"] = node.Name ?? string.Empty,
                ["Type"] = node is ContainerInfo ? "Container" : "Connection",
                ["Expanded"] = node is ContainerInfo { IsExpanded: true },
                // Open sessions are per-client state; the legacy app also always writes false.
                ["Connected"] = false,
                ["ICAEncryptionStrength"] = string.Empty,
                ["InheritICAEncryptionStrength"] = false,
            };

            // Store the node's own values: switch inheritance off while reading them.
            var inheritance = node.Inheritance;
            var flags = InheritProperties.ToDictionary(p => p.Key, p => (bool)p.Value.GetValue(inheritance)!, StringComparer.OrdinalIgnoreCase);
            inheritance.DisableInheritance();
            try
            {
                foreach (var (column, property) in ValueProperties)
                    row[column] = ToDbValue(column, property.GetValue(node), key);
            }
            finally
            {
                inheritance.EnableInheritance();
            }

            foreach (var (column, value) in flags)
                row[column] = _saveFilter.SaveInheritance && value;

            return row;
        }

        private object ToDbValue(string column, object? value, string key)
        {
            switch (column)
            {
                case "Username":
                    return _saveFilter.SaveUsername ? value as string ?? string.Empty : string.Empty;
                case "Domain":
                    return _saveFilter.SaveDomain ? value as string ?? string.Empty : string.Empty;
            }

            if (PasswordColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
            {
                var plain = value as string ?? string.Empty;
                return _saveFilter.SavePassword && plain.Length > 0 ? _crypto.Encrypt(plain, key) : string.Empty;
            }

            return value switch
            {
                null => string.Empty,
                Enum e => e.ToString(),
                bool or int or string => value,
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            };
        }

        // ── Rows → tree ───────────────────────────────────────────────────────

        /// <summary>
        /// Rebuilds the tree from rows (in any order; sorted by PositionID here).
        /// Passwords that cannot be decrypted with <paramref name="key"/> are kept as stored (legacy behaviour).
        /// </summary>
        public ConnectionTreeModel FromRows(IEnumerable<IReadOnlyDictionary<string, object?>> rows, string key, string rootName = "Connections")
        {
            ArgumentNullException.ThrowIfNull(rows);
            var root = new RootNodeInfo(RootNodeType.Connection) { Name = rootName };
            var model = new ConnectionTreeModel(root);

            var ordered = rows
                .Select((row, index) => (Row: row, Index: index))
                .OrderBy(r => ReadInt(r.Row, "PositionID") ?? int.MaxValue)
                .ThenBy(r => r.Index)
                .Select(r => r.Row)
                .ToList();

            var nodes = new List<(ConnectionInfo Node, string ParentId)>();
            var byId = new Dictionary<string, ConnectionInfo>(StringComparer.Ordinal);
            foreach (var row in ordered)
            {
                var node = FromRow(row, key);
                if (node is null || byId.ContainsKey(node.ConstantID))
                    continue;
                byId[node.ConstantID] = node;
                nodes.Add((node, ReadString(row, "ParentID") ?? RootParentId));
            }

            foreach (var (node, parentId) in nodes)
            {
                var parent = byId.TryGetValue(parentId, out var p) && p is ContainerInfo folder && !IsAncestorOrSelf(node, folder)
                    ? folder
                    : root;
                parent.AddChild(node);
            }

            return model;
        }

        /// <summary>Creates a node from a row, or null for an unknown Type.</summary>
        public ConnectionInfo? FromRow(IReadOnlyDictionary<string, object?> row, string key)
        {
            var type = ReadString(row, "Type");
            var id = ReadString(row, "ConstantID");
            if (string.IsNullOrEmpty(id))
                id = Guid.NewGuid().ToString();

            ConnectionInfo node = type switch
            {
                "Connection" => new ConnectionInfo(id),
                "Container" => new ContainerInfo(id),
                _ => null!,
            };
            if (node is null)
                return null;

            node.Name = ReadString(row, "Name") ?? string.Empty;

            foreach (var (column, property) in ValueProperties)
            {
                if (!row.TryGetValue(column, out var raw) || raw is null or DBNull)
                    continue;

                if (PasswordColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                {
                    property.SetValue(node, Decrypt(Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty, key));
                    continue;
                }

                if (TryConvert(raw, property.PropertyType, out var value))
                    property.SetValue(node, value);
            }

            foreach (var (column, property) in InheritProperties)
            {
                if (row.TryGetValue(column, out var raw) && TryConvert(raw, typeof(bool), out var flag))
                    property.SetValue(node.Inheritance, flag);
            }

            if (node is ContainerInfo container)
                container.IsExpanded = ReadBool(row, "Expanded") ?? false;

            return node;
        }

        private string Decrypt(string cipherText, string key)
        {
            if (string.IsNullOrEmpty(cipherText))
                return string.Empty;
            try
            {
                return _crypto.Decrypt(cipherText, key);
            }
            catch (EncryptionException)
            {
                // The value may not be encrypted (legacy behaviour).
                return cipherText;
            }
        }

        private static bool IsAncestorOrSelf(ConnectionInfo node, ContainerInfo candidate)
        {
            for (ConnectionInfo? current = candidate; current is not null; current = current.Parent)
            {
                if (ReferenceEquals(current, node))
                    return true;
            }

            return false;
        }

        // ── Value conversion ──────────────────────────────────────────────────

        public static bool TryConvert(object? raw, Type targetType, out object? value)
        {
            value = null;
            if (raw is null or DBNull)
                return false;

            try
            {
                if (targetType == typeof(string))
                {
                    value = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
                    return true;
                }

                if (targetType == typeof(bool))
                {
                    value = raw switch
                    {
                        bool b => b,
                        string s => s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase),
                        _ => Convert.ToInt64(raw, CultureInfo.InvariantCulture) != 0,
                    };
                    return true;
                }

                if (targetType == typeof(int))
                {
                    value = raw is string text
                        ? int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture)
                        : Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                    return true;
                }

                if (targetType.IsEnum)
                {
                    var text = Convert.ToString(raw, CultureInfo.InvariantCulture);
                    if (string.IsNullOrWhiteSpace(text))
                        return false;
                    if (Enum.TryParse(targetType, text.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(targetType, parsed!))
                    {
                        value = parsed;
                        return true;
                    }

                    return false;
                }

                if (targetType == typeof(DateTime))
                {
                    value = Convert.ToDateTime(raw, CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return false;
            }

            return false;
        }

        private static string? ReadString(IReadOnlyDictionary<string, object?> row, string column) =>
            row.TryGetValue(column, out var raw) && TryConvert(raw, typeof(string), out var value) ? (string)value! : null;

        private static int? ReadInt(IReadOnlyDictionary<string, object?> row, string column) =>
            row.TryGetValue(column, out var raw) && TryConvert(raw, typeof(int), out var value) ? (int)value! : null;

        private static bool? ReadBool(IReadOnlyDictionary<string, object?> row, string column) =>
            row.TryGetValue(column, out var raw) && TryConvert(raw, typeof(bool), out var value) ? (bool)value! : null;

        private static DateTime TruncateToSeconds(DateTime value) =>
            new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);

        private static Dictionary<string, PropertyInfo> BuildValueProperties()
        {
            var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in SqlSchema.ConnectionColumns)
            {
                if (column.Kind == SqlColumnKind.Identity || StructuralColumns.Contains(column.Name)
                    || column.Name.StartsWith("Inherit", StringComparison.Ordinal))
                {
                    continue;
                }

                var propertyName = PropertyAliases.GetValueOrDefault(column.Name, column.Name);
                var property = typeof(ConnectionInfo).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                if (property is { CanRead: true, CanWrite: true })
                    map[column.Name] = property;
            }

            return map;
        }

        private static Dictionary<string, PropertyInfo> BuildInheritProperties()
        {
            var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in SqlSchema.ConnectionColumns)
            {
                if (!column.Name.StartsWith("Inherit", StringComparison.Ordinal) || StructuralColumns.Contains(column.Name))
                    continue;

                var flagName = InheritAliases.GetValueOrDefault(column.Name, column.Name["Inherit".Length..]);
                var property = typeof(ConnectionInfoInheritance).GetProperty(flagName, BindingFlags.Public | BindingFlags.Instance);
                if (property is { CanRead: true, CanWrite: true } && property.PropertyType == typeof(bool))
                    map[column.Name] = property;
            }

            return map;
        }
    }
}
