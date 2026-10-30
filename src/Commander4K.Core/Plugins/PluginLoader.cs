using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Commander4K.PluginSdk;

namespace Commander4K.Core.Plugins;

public sealed class LoadedPlugin
{
    private readonly PluginLoadContext _context;

    internal LoadedPlugin(string assemblyPath, PluginLoadContext context, IReadOnlyList<IPlugin> instances)
    {
        AssemblyPath = assemblyPath;
        _context = context;
        Instances = instances;
    }

    public string AssemblyPath { get; }

    public IReadOnlyList<IPlugin> Instances { get; private set; }

    public bool IsUnloaded { get; private set; }

    /// <summary>
    /// Starts unloading the plugin's load context. The caller must drop every reference to <see cref="Instances"/>
    /// (unregister providers etc.) first; the returned reference turns dead once the GC has collected the context.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public WeakReference Unload()
    {
        Instances = [];
        IsUnloaded = true;
        _context.Unload();
        return new WeakReference(_context);
    }
}

/// <summary>
/// Loads third-party plugins, each into its own collectible <see cref="AssemblyLoadContext"/> so it can carry its own
/// dependency versions and be unloaded at runtime. <c>Commander4K.PluginSdk</c> is always shared with the host so the
/// contract types unify.
/// </summary>
public sealed class PluginLoader
{
    /// <summary>
    /// Loads every <c>plugins/&lt;Name&gt;/&lt;Name&gt;.dll</c> under <paramref name="pluginsRoot"/>. Failures are
    /// returned instead of thrown so one broken plugin cannot stop the others.
    /// </summary>
    public (IReadOnlyList<LoadedPlugin> Loaded, IReadOnlyList<(string Path, Exception Error)> Failed) LoadFromDirectory(string pluginsRoot)
    {
        var loaded = new List<LoadedPlugin>();
        var failed = new List<(string, Exception)>();
        if (!Directory.Exists(pluginsRoot))
        {
            return (loaded, failed);
        }

        foreach (var directory in Directory.EnumerateDirectories(pluginsRoot))
        {
            var assemblyPath = Path.Combine(directory, Path.GetFileName(directory) + ".dll");
            if (!File.Exists(assemblyPath))
            {
                continue;
            }

            try
            {
                loaded.Add(Load(assemblyPath));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed.Add((assemblyPath, ex));
            }
        }

        return (loaded, failed);
    }

    public LoadedPlugin Load(string assemblyPath)
    {
        var fullPath = Path.GetFullPath(assemblyPath);
        var context = new PluginLoadContext(fullPath);
        try
        {
            var assembly = context.LoadFromAssemblyPath(fullPath);
            return new LoadedPlugin(fullPath, context, Instantiate(assembly));
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private static List<IPlugin> Instantiate(Assembly assembly)
    {
        var instances = new List<IPlugin>();
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type is { IsClass: true, IsAbstract: false }
                && typeof(IPlugin).IsAssignableFrom(type)
                && type.GetConstructor(Type.EmptyTypes) is not null)
            {
                instances.Add((IPlugin)Activator.CreateInstance(type)!);
            }
        }

        return instances;
    }
}

internal sealed class PluginLoadContext(string mainAssemblyPath)
    : AssemblyLoadContext(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
{
    private static readonly string SdkAssemblyName = typeof(IPlugin).Assembly.GetName().Name!;

    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, SdkAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            return null; // Fall back to the host's copy.
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}
