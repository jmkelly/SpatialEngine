using System.Reflection;
using System.Runtime.Loader;

namespace Spatial.PluginHost.DotNet.WorkerHost;

/// <summary>
/// Resolves package payloads from the package directory but lets the
/// default context provide every assembly it already knows (Spatial.Core,
/// Spatial.PluginSdk, the runtime, framework assemblies) — both sides of
/// the boundary must share one identity for contract types (ADR-0005), or
/// <c>is</c> checks and method signatures across the boundary would
/// silently break. Preferring the default context first also means a
/// package's bundled contract copies are never double-loaded.
/// </summary>
internal sealed class PackageLoadContext(string packageDirectory)
    : AssemblyLoadContext($"spatial-plugin-{Guid.NewGuid():N}", isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
        }
        catch (FileNotFoundException)
        {
            // Not part of the host's dependency set — a package payload.
        }

        var candidate = Path.Combine(packageDirectory, assemblyName.Name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }
}
