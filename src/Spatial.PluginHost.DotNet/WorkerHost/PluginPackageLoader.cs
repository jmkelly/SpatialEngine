using System.Reflection;
using System.Runtime.Loader;
using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginHost.DotNet.WorkerHost;

/// <summary>
/// Loads one plugin package in the worker process: validates the manifest,
/// loads the plugin assembly in an isolated load context, finds the
/// capability provider (by the manifest's assembly type, or auto-discovered
/// when it is the single public provider) and runs the activation
/// compatibility check between the loaded provider's contract surface and the
/// package manifest (plan §10.4). The plugin never links the worker protocol —
/// the worker host reflects over <see cref="ICapabilityProvider"/> only.
/// </summary>
public static class PluginPackageLoader
{
    public static WorkerProviderSurface Load(string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        var manifest = PluginManifestLoader.Load(packageDirectory);
        var assemblyPath = Path.Combine(packageDirectory, manifest.Assembly);
        if (!File.Exists(assemblyPath))
        {
            throw new PluginManifestException(
                $"The package {Path.GetFileName(packageDirectory)} declares assembly {manifest.Assembly}, "
                + $"but '{assemblyPath}' does not exist; packages must be immutable and self-contained.");
        }

        var loadContext = new PackageLoadContext(Path.GetFullPath(packageDirectory));
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        var provider = ResolveProvider(assembly, manifest);
        ManifestCompatibility.EnsureCompatible(manifest, provider.Id, provider.Descriptors);
        return new WorkerProviderSurface(manifest, provider, loadContext);
    }
    private static ICapabilityProvider ResolveProvider(Assembly assembly, PluginManifest manifest)
    {
        if (manifest.AssemblyType is { Length: > 0 } typeName)
        {
            var type = assembly.GetType(typeName);
            if (type is null)
            {
                throw new PluginManifestException(
                    $"The package declares assemblyType '{typeName}', but the loaded assembly has no such type.");
            }

            return InstantiateProvider(type);
        }

        var candidates = assembly.GetExportedTypes()
            .Where(IsServedProvider)
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new PluginManifestException(
                $"The package does not declare an assemblyType and the assembly contains {candidates.Length} "
                + "public ICapabilityProvider types; declare the assemblyType in the manifest.");
        }

        return InstantiateProvider(candidates[0]);
    }

    private static bool IsServedProvider(Type type) =>
        !type.IsAbstract && IsInstantiableProvider(type);

    private static bool IsInstantiableProvider(Type type) =>
        typeof(ICapabilityProvider).IsAssignableFrom(type)
        && type.GetConstructor(Type.EmptyTypes) is not null;

    private static ICapabilityProvider InstantiateProvider(Type type)
    {
        try
        {
            return (ICapabilityProvider)Activator.CreateInstance(type)!;
        }
        catch (Exception exception)
        {
            throw new PluginManifestException(
                $"cannot instantiate the provider '{type.FullName}': {exception.Message}", exception);
        }
    }
}

/// <summary>The result of loading one package: the validated manifest, the live provider and the load context.</summary>
public sealed class WorkerProviderSurface : IDisposable
{
    internal WorkerProviderSurface(PluginManifest manifest, ICapabilityProvider provider, AssemblyLoadContext loadContext)
    {
        Manifest = manifest;
        Provider = provider;
        _loadContext = loadContext;
    }

    public PluginManifest Manifest { get; }

    public ICapabilityProvider Provider { get; }

    private readonly AssemblyLoadContext _loadContext;

    /// <summary>Unloads the package's load context when the worker exits.</summary>
    public void Dispose() => _loadContext.Unload();
}
