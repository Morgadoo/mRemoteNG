using System.Reflection;
using mRemoteNG.Core.Container;
using mRemoteNG.Core.Tree.Root;

namespace mRemoteNG.Core.Connection
{
    /// <summary>
    /// Reads and writes connection properties by name, separating a node's own value from the value it
    /// inherits from its folder. Used by property editors that show inheritance checkboxes.
    /// </summary>
    public static class ConnectionInheritanceAccessor
    {
        private static readonly string[] NonFlagProperties =
        [
            nameof(ConnectionInfoInheritance.EverythingInherited),
            nameof(ConnectionInfoInheritance.Parent),
            nameof(ConnectionInfoInheritance.InheritanceActive),
        ];

        /// <summary>True when <paramref name="propertyName"/> has an inheritance flag.</summary>
        public static bool SupportsInheritance(string propertyName) =>
            GetFlagProperty(propertyName) is not null;

        /// <summary>
        /// True when a node placed in <paramref name="parent"/> can inherit values:
        /// nodes directly under the root have nothing to inherit from.
        /// </summary>
        public static bool CanInheritFrom(ContainerInfo? parent) =>
            parent is not null and not RootNodeInfo;

        public static bool GetInheritFlag(ConnectionInfo node, string propertyName)
        {
            var flag = GetFlagProperty(propertyName);
            return flag is not null && (bool)flag.GetValue(node.Inheritance)!;
        }

        public static void SetInheritFlag(ConnectionInfo node, string propertyName, bool value)
        {
            var flag = GetFlagProperty(propertyName)
                ?? throw new ArgumentException($"{propertyName} cannot be inherited.", nameof(propertyName));
            flag.SetValue(node.Inheritance, value);
        }

        /// <summary>The value the property resolves to (inherited when the flag is on).</summary>
        public static T GetValue<T>(ConnectionInfo node, string propertyName) =>
            (T)GetProperty(propertyName).GetValue(node)!;

        /// <summary>The node's own value, ignoring any inheritance flag.</summary>
        public static T GetOwnValue<T>(ConnectionInfo node, string propertyName)
        {
            var flag = GetFlagProperty(propertyName);
            if (flag is null || !(bool)flag.GetValue(node.Inheritance)!)
                return GetValue<T>(node, propertyName);

            flag.SetValue(node.Inheritance, false);
            try
            {
                return GetValue<T>(node, propertyName);
            }
            finally
            {
                flag.SetValue(node.Inheritance, true);
            }
        }

        /// <summary>Sets the node's own value (the inheritance flag is left unchanged).</summary>
        public static void SetOwnValue<T>(ConnectionInfo node, string propertyName, T value) =>
            GetProperty(propertyName).SetValue(node, value);

        private static PropertyInfo GetProperty(string propertyName) =>
            typeof(ConnectionInfo).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new ArgumentException($"ConnectionInfo has no property {propertyName}.", nameof(propertyName));

        private static PropertyInfo? GetFlagProperty(string propertyName)
        {
            if (NonFlagProperties.Contains(propertyName))
                return null;
            var flag = typeof(ConnectionInfoInheritance).GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            return flag is { PropertyType: var t, CanWrite: true } && t == typeof(bool) ? flag : null;
        }
    }
}
