using System.Diagnostics.CodeAnalysis;
using Spatial.PluginHost.DotNet.Manifest;

namespace Spatial.PluginHost.DotNet.Supervision;

/// <summary>One discovered, immutable plugin package: its directory and its validated manifest.</summary>
public sealed record PluginPackage(string Path, PluginManifest Manifest);

/// <summary>
/// Package discovery (plan §10.2/§10.4): scans a packages root for plugin
/// packages — subdirectories carrying a valid <c>manifest.json</c> — so
/// side-by-side versions are sibling package directories. A directory that is
/// not a valid package is skipped with its problems recorded, never fatal to
/// the scan.
/// </summary>
public static class PluginDiscovery
{
    /// <summary>
    /// Discovers every valid package directly under <paramref name="packagesRoot"/>,
    /// ordered by provider id. Returns an empty list when the root does not exist.
    /// </summary>
    public static IReadOnlyList<PluginPackage> Discover(string packagesRoot)
    {
        var found = new List<PluginPackage>();
        if (string.IsNullOrWhiteSpace(packagesRoot) || !Directory.Exists(packagesRoot))
        {
            return found;
        }

        foreach (var directory in Directory.EnumerateDirectories(packagesRoot))
        {
            if (TryLoadPackage(directory, out var package, out _))
            {
                found.Add(package!);
            }
        }

        return found.OrderBy(pluginPackage => pluginPackage.Manifest.Id).ToArray();
    }

    /// <summary>Loads one package directory without throwing: false plus problems when it is not a valid package.</summary>
    public static bool TryLoadPackage(
        string packageDirectory,
        [NotNullWhen(true)] out PluginPackage? package,
        out IReadOnlyList<string> problems)
    {
        if (PluginManifestLoader.TryLoad(packageDirectory, out var manifest, out problems))
        {
            package = new PluginPackage(packageDirectory, manifest!);
            return true;
        }

        package = null;
        return false;
    }
}
