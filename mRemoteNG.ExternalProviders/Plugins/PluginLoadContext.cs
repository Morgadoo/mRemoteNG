using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace mRemoteNG.ExternalProviders.Plugins;

/// <summary>
/// Loads one plugin assembly and its private dependencies (from its .deps.json, or the files next to it) apart from
/// the app and other plugins. The contracts assembly — and System.Drawing.Common, whose Image appears in the
/// contracts — always come from the app so plugin and host agree on the types.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    public const string ContractsAssemblyName = "mRp.Contracts";

    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        ContractsAssemblyName,
        "System.Drawing.Common",
    };

    private readonly string _directory;
    private readonly AssemblyDependencyResolver? _resolver;

    public PluginLoadContext(string pluginPath)
        : base("Plugin: " + Path.GetFileNameWithoutExtension(pluginPath))
    {
        _directory = Path.GetDirectoryName(pluginPath) ?? ".";
        try
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }
        catch (InvalidOperationException)
        {
            // A broken .deps.json: fall back to the files next to the plugin.
        }
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null || Shared.Contains(assemblyName.Name))
            return null;

        var path = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            var local = Path.Combine(_directory, assemblyName.Name + ".dll");
            // Framework assemblies resolve from the app; a copy next to the plugin must not shadow them.
            if (File.Exists(local) && !IsFrameworkAssembly(assemblyName))
                path = local;
        }
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    private static bool IsFrameworkAssembly(AssemblyName name)
    {
        try
        {
            Default.LoadFromAssemblyName(name);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is a .NET assembly that references the plugin contracts — read from its
    /// metadata, so unrelated and native DLLs in a plugin folder are never loaded.
    /// </summary>
    public static bool ReferencesContracts(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return false;
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
                return false;
            if (string.Equals(reader.GetString(reader.GetAssemblyDefinition().Name), ContractsAssemblyName, StringComparison.OrdinalIgnoreCase))
                return false;
            return reader.AssemblyReferences
                .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
                .Any(name => string.Equals(name, ContractsAssemblyName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }
}
