using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginHost.DotNet.WorkerHost;

namespace Spatial.PluginHost.DotNet.Tests;

/// <summary>
/// The package-loading failure surface (plan §10.4): a package whose declared
/// assembly is missing, whose <c>assemblyType</c> names a type that does not
/// exist, or that omits <c>assemblyType</c> while the assembly exports an
/// ambiguous provider set must fail with an actionable message — a plugin
/// can never silently load the wrong provider. Each case loads a real package
/// directory (manifest + fixture assembly) in-process, same as the worker.
/// </summary>
public sealed class PluginPackageLoaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("spatial-package-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Load_requires_an_existing_declared_assembly()
    {
        var directory = PackageWriter.Write(_root, FixtureManifest.V1());
        File.Delete(Path.Combine(directory, FixtureManifest.AssemblyName));

        var exception = Assert.Throws<PluginManifestException>(() => PluginPackageLoader.Load(directory));

        Assert.Contains("declares assembly", exception.Message);
        Assert.Contains(FixtureManifest.AssemblyName, exception.Message);
        Assert.Contains("does not exist", exception.Message);
    }

    [Fact]
    public void Declared_assembly_type_that_does_not_exist_is_an_actionable_error()
    {
        var directory = PackageWriter.Write(
            _root, FixtureManifest.V1() with { AssemblyType = "Spatial.Plugin.Fixtures.NoSuchProvider" });

        var exception = Assert.Throws<PluginManifestException>(() => PluginPackageLoader.Load(directory));

        Assert.Contains("has no such type", exception.Message);
        Assert.Contains("Spatial.Plugin.Fixtures.NoSuchProvider", exception.Message);
    }

    [Fact]
    public void Missing_assembly_type_with_an_ambiguous_provider_set_is_an_actionable_error()
    {
        // The fixture assembly exports both FixtureProviderV1 and FixtureProviderV2.
        var directory = PackageWriter.Write(_root, FixtureManifest.V1() with { AssemblyType = null });

        var exception = Assert.Throws<PluginManifestException>(() => PluginPackageLoader.Load(directory));

        Assert.Contains("does not declare an assemblyType", exception.Message);
        Assert.Contains("2 public ICapabilityProvider types", exception.Message);
    }
}
