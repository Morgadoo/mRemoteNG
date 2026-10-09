using System.Globalization;
using System.Reflection;
using mRemoteNG.Core.Config.Serializers.Misc;
using mRemoteNG.Core.Connection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Config.Serializers.Csv
{
    /// <summary>
    /// Reads the mRemoteNG CSV format written by <see cref="CsvConnectionsSerializerMremotengFormat"/>
    /// and by the legacy WinForms app. Columns are matched by header name, so files exported with
    /// credentials or inheritance left out are read as well.
    /// </summary>
    public class CsvConnectionsDeserializerMremotengFormat : IDeserializer<string, ConnectionTreeModel>
    {
        /// <summary>The legacy writer forgot the ';' between these two headers.</summary>
        private const string LegacyMergedHeader = "RedirectDiskDrivesCustomRedirectPorts";

        private static readonly Dictionary<string, PropertyInfo> ConnectionProperties = typeof(ConnectionInfo)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && IsSupportedType(p.PropertyType))
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, PropertyInfo> InheritanceProperties = typeof(ConnectionInfoInheritance)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.PropertyType == typeof(bool))
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> StructuralColumns = new(StringComparer.OrdinalIgnoreCase)
        {
            "Id", "Parent", "NodeType"
        };

        private readonly List<string> _warnings = [];

        /// <summary>Problems found in the last file read (skipped rows, unreadable values).</summary>
        public IReadOnlyList<string> Warnings => _warnings;

        public ConnectionTreeModel Deserialize(string serializedData)
        {
            ArgumentNullException.ThrowIfNull(serializedData);
            _warnings.Clear();

            var lines = serializedData.TrimStart('﻿')
                .Split(["\r\n", "\r", "\n"], StringSplitOptions.RemoveEmptyEntries);

            var root = new RootNodeInfo(RootNodeType.Connection);
            if (lines.Length == 0)
                return new ConnectionTreeModel(root);

            var headers = ReadHeaders(lines[0]);
            var idColumn = headers.IndexOf("Id");
            var parentColumn = headers.IndexOf("Parent");
            var nodeTypeColumn = headers.IndexOf("NodeType");

            var nodes = new List<(ConnectionInfo Node, string ParentId)>();
            var containersById = new Dictionary<string, ContainerInfo>(StringComparer.OrdinalIgnoreCase);
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var lineNumber = 1; lineNumber < lines.Length; lineNumber++)
            {
                var fields = lines[lineNumber].Split(';');
                if (fields.All(string.IsNullOrWhiteSpace))
                    continue;

                var id = Get(fields, idColumn);
                if (string.IsNullOrWhiteSpace(id) || !usedIds.Add(id))
                    id = Guid.NewGuid().ToString();

                var isContainer = Enum.TryParse(Get(fields, nodeTypeColumn), true, out TreeNodeType nodeType)
                    && nodeType == TreeNodeType.Container;

                ConnectionInfo node = isContainer
                    ? ConnectionDefaults.NewContainer("", id)
                    : ConnectionDefaults.NewConnection(id: id);

                PopulateNode(node, headers, fields, lineNumber + 1);

                if (node is ContainerInfo container)
                    containersById[container.ConstantID] = container;
                nodes.Add((node, Get(fields, parentColumn)));
            }

            foreach (var (node, parentId) in nodes)
            {
                if (!string.IsNullOrEmpty(parentId)
                    && containersById.TryGetValue(parentId, out var parent)
                    && !IsSelfOrAncestor(node, parent))
                {
                    parent.AddChild(node);
                }
                else
                {
                    root.AddChild(node);
                }
            }

            return new ConnectionTreeModel(root);
        }

        private static List<string> ReadHeaders(string headerLine)
        {
            var headers = headerLine.Split(';').Select(h => h.Trim()).ToList();

            var mergedIndex = headers.IndexOf(LegacyMergedHeader);
            if (mergedIndex < 0)
                return headers;

            // A file from the legacy app: split the merged column, and use the order in which the
            // legacy writer really wrote these three inheritance values.
            headers[mergedIndex] = "RedirectDiskDrivesCustom";
            headers.Insert(mergedIndex + 1, "RedirectPorts");

            var userViaApi = headers.IndexOf("InheritUserViaAPI");
            if (userViaApi >= 0
                && userViaApi + 2 < headers.Count
                && headers[userViaApi + 1] == "InheritRedirectAudioCapture"
                && headers[userViaApi + 2] == "InheritRdpVersion")
            {
                headers[userViaApi] = "InheritRedirectAudioCapture";
                headers[userViaApi + 1] = "InheritRdpVersion";
                headers[userViaApi + 2] = "InheritUserViaAPI";
            }

            return headers;
        }

        private void PopulateNode(ConnectionInfo node, IReadOnlyList<string> headers, string[] fields, int lineNumber)
        {
            var hasPort = false;
            for (var column = 0; column < headers.Count && column < fields.Length; column++)
            {
                var header = headers[column];
                var value = fields[column];
                if (header.Length == 0 || StructuralColumns.Contains(header))
                    continue;

                if (header.StartsWith("Inherit", StringComparison.OrdinalIgnoreCase)
                    && InheritanceProperties.TryGetValue(header["Inherit".Length..], out var inheritProperty))
                {
                    if (bool.TryParse(value, out var inherit))
                        inheritProperty.SetValue(node.Inheritance, inherit);
                    continue;
                }

                var propertyName = header.Equals("ConnectToConsole", StringComparison.OrdinalIgnoreCase)
                    ? nameof(ConnectionInfo.UseConsoleSession)
                    : header;
                if (!ConnectionProperties.TryGetValue(propertyName, out var property))
                    continue;

                if (TryConvert(value, property.PropertyType, out var converted))
                {
                    property.SetValue(node, converted);
                    hasPort |= property.Name == nameof(ConnectionInfo.Port);
                }
                else if (!string.IsNullOrEmpty(value))
                {
                    _warnings.Add($"Line {lineNumber}: ignored invalid value \"{value}\" for {header}.");
                }
            }

            if (!hasPort)
                node.Port = ConnectionInfo.GetDefaultPort(node.Protocol);
        }

        private static bool IsSupportedType(Type type) =>
            type == typeof(string) || type == typeof(int) || type == typeof(bool) || type.IsEnum;

        private static bool TryConvert(string value, Type type, out object? result)
        {
            result = null;
            if (type == typeof(string))
            {
                result = value;
                return true;
            }

            if (type == typeof(int))
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                    return false;
                result = number;
                return true;
            }

            if (type == typeof(bool))
            {
                if (!bool.TryParse(value, out var flag))
                    return false;
                result = flag;
                return true;
            }

            if (type.IsEnum && Enum.TryParse(type, value, ignoreCase: true, out var enumValue))
            {
                result = enumValue;
                return true;
            }

            return false;
        }

        private static bool IsSelfOrAncestor(ConnectionInfo node, ContainerInfo candidateParent)
        {
            for (ConnectionInfo? current = candidateParent; current is not null; current = current.Parent)
            {
                if (ReferenceEquals(current, node))
                    return true;
            }
            return false;
        }

        private static string Get(string[] fields, int index) =>
            index >= 0 && index < fields.Length ? fields[index] : "";
    }
}
