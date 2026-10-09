using System.Globalization;
using System.Text.Json;
using mRemoteNG.Core.Connection.Protocol;

namespace mRemoteNG.Core.Connection
{
    /// <summary>
    /// The "default connection" and "default inheritance" the legacy app kept in its settings
    /// (ConDefault* / InhDefault*): values and inheritance flags given to every new connection and folder.
    /// Stored as two strings: a JSON object of property values and a comma-separated list of inherited
    /// properties. Name, host name and secrets are never stored (settings are not encrypted).
    /// </summary>
    public static class DefaultConnectionSettings
    {
        /// <summary>Properties that are never part of the defaults.</summary>
        public static IReadOnlySet<string> ExcludedProperties { get; } = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(ConnectionInfo.Name),
            nameof(ConnectionInfo.Hostname),
            nameof(ConnectionInfo.Password),
            nameof(ConnectionInfo.RDGatewayPassword),
            nameof(ConnectionInfo.VNCProxyPassword),
        };

        /// <summary>Catalog properties that can have a default value.</summary>
        public static IEnumerable<ConnectionPropertyDescriptor> DefaultableProperties =>
            ConnectionPropertyCatalog.All.Where(d => !ExcludedProperties.Contains(d.Name));

        /// <summary>
        /// The built-in defaults used until the user edits the default connection: the legacy
        /// ConDefault* values and SSH2 as the protocol.
        /// </summary>
        public static ConnectionInfo CreateBuiltInDefaults()
        {
            var info = ConnectionDefaults.ApplyNewConnectionDefaults(new ConnectionInfo { Name = "Default connection" });
            info.Protocol = ProtocolType.SSH2;
            info.Port = ConnectionInfo.GetDefaultPort(info.Protocol);
            info.IsDefault = true;
            return info;
        }

        /// <summary>Builds the default-connection template from the stored strings (invalid entries are ignored).</summary>
        public static ConnectionInfo Load(string? values, string? inheritance)
        {
            var template = CreateBuiltInDefaults();
            if (!string.IsNullOrWhiteSpace(values))
            {
                Dictionary<string, string>? map;
                try
                {
                    map = JsonSerializer.Deserialize<Dictionary<string, string>>(values);
                }
                catch (JsonException)
                {
                    map = null;
                }

                foreach (var (name, raw) in map ?? [])
                {
                    if (ExcludedProperties.Contains(name) || !ConnectionPropertyCatalog.TryGet(name, out var descriptor))
                        continue;
                    if (TryParse(raw, descriptor.PropertyType, out var value))
                        descriptor.Property.SetValue(template, value);
                }
            }

            foreach (var name in ParseInheritance(inheritance))
                ConnectionInheritanceAccessor.SetInheritFlag(template, name, true);

            return template;
        }

        /// <summary>Serialises <paramref name="template"/>'s values and inheritance flags for storage.</summary>
        public static (string Values, string Inheritance) Save(ConnectionInfo template)
        {
            ArgumentNullException.ThrowIfNull(template);
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var descriptor in DefaultableProperties)
            {
                var value = ConnectionInheritanceAccessor.GetOwnValue<object?>(template, descriptor.Name);
                map[descriptor.Name] = Format(value);
            }

            var inherited = template.Inheritance.GetProperties()
                .Where(p => p.PropertyType == typeof(bool) && (bool)p.GetValue(template.Inheritance)!)
                .Select(p => p.Name);
            return (JsonSerializer.Serialize(map), string.Join(",", inherited));
        }

        /// <summary>
        /// Gives a new node the default values and inheritance flags. Name and host name are kept;
        /// the port follows the default protocol unless the defaults set a custom one.
        /// </summary>
        public static T ApplyTo<T>(ConnectionInfo template, T target) where T : ConnectionInfo
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(target);
            foreach (var descriptor in DefaultableProperties)
            {
                var value = ConnectionInheritanceAccessor.GetOwnValue<object?>(template, descriptor.Name);
                descriptor.Property.SetValue(target, value);
            }

            ApplyInheritance(template, target);
            return target;
        }

        /// <summary>Copies only the inheritance flags (legacy "Apply default inheritance").</summary>
        public static void ApplyInheritance(ConnectionInfo template, ConnectionInfo target)
        {
            foreach (var flag in target.Inheritance.GetProperties().Where(p => p.PropertyType == typeof(bool) && p.CanWrite))
                flag.SetValue(target.Inheritance, flag.GetValue(template.Inheritance));
        }

        private static IEnumerable<string> ParseInheritance(string? inheritance) =>
            (inheritance ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(ConnectionInheritanceAccessor.SupportsInheritance);

        private static string Format(object? value) => value switch
        {
            null => "",
            bool b => b ? "true" : "false",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };

        private static bool TryParse(string raw, Type type, out object? value)
        {
            value = null;
            if (type == typeof(string))
            {
                value = raw;
                return true;
            }
            if (type == typeof(bool))
            {
                if (!bool.TryParse(raw, out var b)) return false;
                value = b;
                return true;
            }
            if (type == typeof(int))
            {
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return false;
                value = i;
                return true;
            }
            if (type.IsEnum)
            {
                if (!Enum.TryParse(type, raw, ignoreCase: true, out var e) || !Enum.IsDefined(type, e!)) return false;
                value = e;
                return true;
            }
            return false;
        }
    }
}
