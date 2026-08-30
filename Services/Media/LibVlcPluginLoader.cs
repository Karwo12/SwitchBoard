using System.Reflection;
using System.Runtime.Loader;
using System.IO;
using SwitchBoard.Controls;

namespace SwitchBoard.Services.Media;

/// <summary>
/// Loads the optional implementation only after the package has passed the
/// component service validation. The main executable never references
/// LibVLCSharp or the native VLC runtime.
/// </summary>
public sealed class LibVlcPluginLoader
{
    private readonly ILibVlcComponentService _componentService;
    private readonly object _gate = new();
    private IVideoBackgroundRendererFactory? _factory;
    private Exception? _loadFailure;

    public LibVlcPluginLoader(ILibVlcComponentService componentService) => _componentService = componentService;

    public bool IsRuntimeLoaded => _factory is not null;

    public bool IsInstalled => _componentService.GetStatus().IsInstalled;

    public bool TryCreateRenderer(out IVideoBackgroundRenderer? renderer, out Exception? error)
    {
        renderer = null;
        var status = _componentService.GetStatus();
        if (!status.IsInstalled)
        {
            error = new InvalidOperationException(status.Detail ?? "The optional LibVLC component is not installed.");
            return false;
        }

        lock (_gate)
        {
            if (_factory is null && _loadFailure is null)
            {
                try { _factory = LoadFactory(status.ComponentDirectory); }
                catch (Exception exception) { _loadFailure = exception; }
            }
            if (_factory is null)
            {
                error = _loadFailure ?? new InvalidOperationException("The LibVLC component could not be loaded.");
                return false;
            }

            try
            {
                // Once the optional factory is invoked, native dependencies may
                // become loaded even if its construction ultimately fails. Keep
                // removal deferred to the next launch in that case too.
                if (_componentService is LibVlcComponentService concrete) concrete.MarkRuntimeLoaded();
                renderer = _factory.Create(status.ComponentDirectory);
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = exception;
                return false;
            }
        }
    }

    private static IVideoBackgroundRendererFactory LoadFactory(string componentDirectory)
    {
        var entryPath = Path.Combine(componentDirectory, LibVlcComponentDescriptor.EntryAssemblyName);
        if (!File.Exists(entryPath)) throw new FileNotFoundException("The LibVLC plugin assembly is missing.", entryPath);
        var context = new LibVlcPluginLoadContext(entryPath);
        var assembly = context.LoadFromAssemblyPath(entryPath);
        var factoryType = assembly.GetType(LibVlcComponentDescriptor.EntryTypeName, throwOnError: false) ??
            assembly.GetTypes().FirstOrDefault(type => !type.IsAbstract && typeof(IVideoBackgroundRendererFactory).IsAssignableFrom(type));
        if (factoryType is null)
            throw new InvalidDataException("The LibVLC plugin does not expose a compatible renderer factory.");
        if (Activator.CreateInstance(factoryType) is not IVideoBackgroundRendererFactory factory)
            throw new InvalidDataException("The LibVLC renderer factory could not be created.");
        return factory;
    }

    private sealed class LibVlcPluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _componentDirectory;

        public LibVlcPluginLoadContext(string entryAssemblyPath) : base("SwitchBoard.LibVlcPlugin", isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
            _componentDirectory = Path.GetDirectoryName(entryAssemblyPath) ?? throw new InvalidOperationException(
                "The LibVLC component entry assembly does not have a parent directory.");
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, typeof(IVideoBackgroundRendererFactory).Assembly.GetName().Name,
                StringComparison.OrdinalIgnoreCase))
                return typeof(IVideoBackgroundRendererFactory).Assembly;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is not null) return LoadFromAssemblyPath(path);

            // The companion ZIP deliberately contains only the runtime DLLs rather
            // than an entire NuGet cache layout. Fall back to its verified root for
            // managed plugin dependencies such as LibVLCSharp.dll.
            var fallback = Path.Combine(_componentDirectory, assemblyName.Name + ".dll");
            return File.Exists(fallback) ? LoadFromAssemblyPath(fallback) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
