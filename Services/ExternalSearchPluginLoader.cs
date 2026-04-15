using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using SpotCont.Plugins;

namespace SpotCont.Services;

public sealed class ExternalSearchPluginLoader
{
    private readonly string _extensionsDirectory;
    private readonly object _syncRoot = new();
    private bool _resolverAttached;

    public ExternalSearchPluginLoader(string extensionsDirectory)
    {
        _extensionsDirectory = extensionsDirectory;
    }

    public IReadOnlyList<ISearchPlugin> LoadPlugins(SearchPluginContext context)
    {
        Directory.CreateDirectory(_extensionsDirectory);
        EnsureResolverAttached();

        var plugins = new List<ISearchPlugin>();
        var loadedPluginTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assemblyPath in Directory.EnumerateFiles(_extensionsDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var assembly = LoadAssembly(assemblyPath);
            if (assembly is null)
            {
                continue;
            }

            foreach (var plugin in CreatePluginsFromAssembly(assembly, context, loadedPluginTypes))
            {
                plugins.Add(plugin);
            }
        }

        return plugins;
    }

    private void EnsureResolverAttached()
    {
        if (_resolverAttached)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_resolverAttached)
            {
                return;
            }

            AssemblyLoadContext.Default.Resolving += Default_Resolving;
            _resolverAttached = true;
        }
    }

    private Assembly? Default_Resolving(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        var candidatePath = Path.Combine(_extensionsDirectory, $"{assemblyName.Name}.dll");
        if (!File.Exists(candidatePath))
        {
            return null;
        }

        try
        {
            return context.LoadFromAssemblyPath(candidatePath);
        }
        catch
        {
            return null;
        }
    }

    private static Assembly? LoadAssembly(string assemblyPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(assemblyPath);
            var existingAssembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(assembly =>
                    !string.IsNullOrWhiteSpace(assembly.Location) &&
                    string.Equals(Path.GetFullPath(assembly.Location), fullPath, StringComparison.OrdinalIgnoreCase));

            return existingAssembly ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<ISearchPlugin> CreatePluginsFromAssembly(
        Assembly assembly,
        SearchPluginContext context,
        ISet<string> loadedPluginTypes)
    {
        var types = GetLoadableTypes(assembly);

        foreach (var factoryType in types.Where(type =>
                     type is { IsAbstract: false, IsInterface: false } &&
                     typeof(ISearchPluginFactory).IsAssignableFrom(type)))
        {
            ISearchPluginFactory? factory;
            try
            {
                factory = Activator.CreateInstance(factoryType) as ISearchPluginFactory;
            }
            catch
            {
                continue;
            }

            if (factory is null)
            {
                continue;
            }

            IEnumerable<ISearchPlugin> createdPlugins;
            try
            {
                createdPlugins = factory.CreatePlugins(context);
            }
            catch
            {
                continue;
            }

            foreach (var plugin in createdPlugins.Where(plugin => plugin is not null))
            {
                var pluginTypeName = plugin.GetType().FullName ?? plugin.Metadata.Id;
                if (loadedPluginTypes.Add(pluginTypeName))
                {
                    yield return plugin;
                }
            }
        }

        foreach (var pluginType in types.Where(type =>
                     type is { IsAbstract: false, IsInterface: false } &&
                     typeof(ISearchPlugin).IsAssignableFrom(type) &&
                     !typeof(ISearchPluginFactory).IsAssignableFrom(type)))
        {
            if (pluginType.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            ISearchPlugin? plugin;
            try
            {
                plugin = Activator.CreateInstance(pluginType) as ISearchPlugin;
            }
            catch
            {
                continue;
            }

            if (plugin is null)
            {
                continue;
            }

            var pluginTypeName = pluginType.FullName ?? plugin.Metadata.Id;
            if (loadedPluginTypes.Add(pluginTypeName))
            {
                yield return plugin;
            }
        }
    }

    private static IReadOnlyList<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types
                .Where(type => type is not null)
                .Cast<Type>()
                .ToArray();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }
}
