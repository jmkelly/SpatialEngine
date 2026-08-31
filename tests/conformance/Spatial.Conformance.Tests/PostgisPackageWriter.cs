using Spatial.PluginHost.DotNet.Manifest;
using Spatial.Provider.PostGIS;

namespace Spatial.Conformance.Tests;

/// <summary>
/// Writes an immutable PostGIS plugin package directory: the manifest plus
/// the provider assembly, its contracts, Core/PluginSdk and Npgsql 10 with
/// its logging dependency (the worker host loads them from the package
/// directory because the host process does not reference Npgsql).
/// </summary>
internal static class PostgisPackageWriter
{
    public static string Write(string root)
    {
        var manifest = PostgisManifest.Build();
        var directory = Path.Combine(root, manifest.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PluginManifest.FileName), PostgisManifest.ToJson(manifest));

        var pluginDirectory = Path.GetDirectoryName(typeof(PostgisProvider).Assembly.Location)!;
        foreach (var (source, name) in new[]
        {
            (typeof(PostgisProvider).Assembly.Location, "Spatial.Provider.PostGIS.dll"),
            (typeof(Spatial.Core.Features.FeatureId).Assembly.Location, "Spatial.Core.dll"),
            (typeof(Spatial.PluginSdk.Capabilities.ICapabilityProvider).Assembly.Location, "Spatial.PluginSdk.dll"),
            (Path.Combine(pluginDirectory, "Npgsql.dll"), "Npgsql.dll"),
            (Path.Combine(pluginDirectory, "Microsoft.Extensions.Logging.Abstractions.dll"), "Microsoft.Extensions.Logging.Abstractions.dll"),
        })
        {
            File.Copy(source, Path.Combine(directory, name));
        }

        return directory;
    }
}
