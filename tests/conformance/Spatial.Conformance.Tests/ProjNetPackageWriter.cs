using Spatial.PluginHost.DotNet.Manifest;
using Spatial.Transformations.ProjNet;

namespace Spatial.Conformance.Tests;

/// <summary>
/// Writes an immutable ProjNet plugin package directory: the manifest plus
/// the provider assembly, its contracts and ProjNET itself (the worker host
/// loads ProjNET from the package directory because the host process does not
/// reference it).
/// </summary>
internal static class ProjNetPackageWriter
{
    public static string Write(string root)
    {
        var manifest = ProjNetManifest.Build();
        var directory = Path.Combine(root, manifest.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PluginManifest.FileName), ProjNetManifest.ToJson(manifest));

        var outputDirectory = Path.GetDirectoryName(typeof(ProjNetTransformationsProvider).Assembly.Location)!;
        var projNetDirectory = Path.GetDirectoryName(typeof(ProjNet.CoordinateSystems.CoordinateSystem).Assembly.Location)!;
        foreach (var (source, name) in new[]
        {
            (typeof(ProjNetTransformationsProvider).Assembly.Location, "Spatial.Transformations.ProjNet.dll"),
            (Path.Combine(projNetDirectory, "ProjNET.dll"), "ProjNET.dll"),
            (typeof(Spatial.Core.Geometry.IGeometry).Assembly.Location, "Spatial.Core.dll"),
            (typeof(Spatial.PluginSdk.Capabilities.ICapabilityProvider).Assembly.Location, "Spatial.PluginSdk.dll"),
        })
        {
            File.Copy(source, Path.Combine(directory, name));
        }

        return directory;
    }
}
