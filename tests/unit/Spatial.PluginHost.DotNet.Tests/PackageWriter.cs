using Spatial.Plugin.Fixtures;
using Spatial.PluginHost.DotNet.Manifest;

namespace Spatial.PluginHost.DotNet.Tests;

/// <summary>
/// Writes a plugin package directory: <c>manifest.json</c> plus the fixture
/// assembly and its contract assemblies (the package is self-contained; the
/// worker host prefers the shared identities when loading, see
/// PluginPackageLoader). Used by both the direct worker-process rig and the
/// supervisor rig.
/// </summary>
internal static class PackageWriter
{
    public static string Write(string root, PluginManifest manifest)
    {
        var directory = Path.Combine(root, manifest.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PluginManifest.FileName), FixtureManifest.ToJson(manifest));

        var fixtureDirectory = Path.GetDirectoryName(typeof(FixtureProviderV1).Assembly.Location)!;
        foreach (var assembly in new[] { "Spatial.Plugin.Fixtures.dll", "Spatial.Core.dll", "Spatial.PluginSdk.dll" })
        {
            File.Copy(Path.Combine(fixtureDirectory, assembly), Path.Combine(directory, assembly));
        }

        return directory;
    }
}
