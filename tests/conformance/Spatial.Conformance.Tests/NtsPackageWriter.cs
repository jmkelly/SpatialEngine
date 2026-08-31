using Spatial.Operations.NetTopologySuite;
using Spatial.PluginHost.DotNet.Manifest;

namespace Spatial.Conformance.Tests;

/// <summary>
/// Writes an immutable NetTopologySuite plugin package directory: the
/// manifest plus the provider assembly, its contracts and NetTopologySuite
/// itself (the worker host loads NTS from the package directory because the
/// host process does not reference it).
/// </summary>
internal static class NtsPackageWriter
{
    public static string Write(string root)
    {
        var manifest = NtsManifest.Build();
        var directory = Path.Combine(root, manifest.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PluginManifest.FileName), NtsManifest.ToJson(manifest));

        var outputDirectory = Path.GetDirectoryName(typeof(NtsOperationsProvider).Assembly.Location)!;
        var ntsDirectory = Path.GetDirectoryName(typeof(NetTopologySuite.Geometries.Geometry).Assembly.Location)!;
        foreach (var (source, name) in new[]
        {
            (typeof(NtsOperationsProvider).Assembly.Location, "Spatial.Operations.NetTopologySuite.dll"),
            (Path.Combine(ntsDirectory, "NetTopologySuite.dll"), "NetTopologySuite.dll"),
            (typeof(Spatial.Core.Features.FeatureId).Assembly.Location, "Spatial.Core.dll"),
            (typeof(Spatial.PluginSdk.Capabilities.ICapabilityProvider).Assembly.Location, "Spatial.PluginSdk.dll"),
        })
        {
            File.Copy(source, Path.Combine(directory, name));
        }

        return directory;
    }
}
