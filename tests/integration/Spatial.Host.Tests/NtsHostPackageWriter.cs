using System.Text.Json;
using Spatial.Operations.NetTopologySuite;
using Spatial.PluginHost.DotNet.Manifest;
using Spatial.Provider.Demo;

namespace Spatial.Host.Tests;

/// <summary>
/// Writes immutable plugin worker packages for the host's supervisor: the
/// manifest plus the provider assembly, its contracts and its runtime
/// dependencies (the worker host loads those from the package directory
/// because the host process does not reference them). Mirrors the conformance
/// suite's package writers so the host integration tests prove the full
/// isolated-worker path over HTTP. Phase 10 (ADR-0031) adds the side-by-side
/// <c>nts@2</c> version and the <c>demo@1</c> data provider — the packages
/// the replacement demonstration and the workbench tests drive.
/// </summary>
internal static class NtsHostPackageWriter
{
    /// <summary>Writes the single <c>nts@1</c> package (the Phase 9 shape).</summary>
    public static string Write(string root) => WritePackage(root, new NtsOperationsProvider(),
        "Spatial.Operations.NetTopologySuite.NtsOperationsProvider", "NetTopologySuite operations");

    /// <summary>Writes both released NTS versions (<c>nts@1</c> and <c>nts@2</c>) side by side.</summary>
    public static void WritePair(string root)
    {
        WritePackage(root, new NtsOperationsProvider(),
            "Spatial.Operations.NetTopologySuite.NtsOperationsProvider", "NetTopologySuite operations");
        WritePackage(root, new NtsOperationsProviderV2(),
            "Spatial.Operations.NetTopologySuite.NtsOperationsProviderV2", "NetTopologySuite operations v2");
    }

    /// <summary>Writes the demo data provider package alongside the NTS ones.</summary>
    public static void WriteDemo(string root) => WritePackage(root, new DemoProvider(),
        "Spatial.Provider.Demo.DemoProvider", "Demo data provider");

    private static string WritePackage(
        string root,
        Spatial.PluginSdk.Capabilities.ICapabilityProvider provider,
        string assemblyType,
        string displayName)
    {
        var manifest = BuildManifest(provider, assemblyType, displayName);
        var directory = Path.Combine(root, manifest.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PluginManifest.FileName), ToJson(manifest));

        var assemblyName = provider.GetType().Assembly.GetName().Name + ".dll";
        var outputDirectory = Path.GetDirectoryName(provider.GetType().Assembly.Location)!;
        var ntsDirectory = Path.GetDirectoryName(typeof(NetTopologySuite.Geometries.Geometry).Assembly.Location)!;
        (string Source, string Name)[] payload =
        [
            (Path.Combine(outputDirectory, assemblyName), assemblyName),
            (Path.Combine(ntsDirectory, "NetTopologySuite.dll"), "NetTopologySuite.dll"),
            (typeof(Spatial.Core.Geometry.IGeometry).Assembly.Location, "Spatial.Core.dll"),
            (typeof(Spatial.PluginSdk.Capabilities.ICapabilityProvider).Assembly.Location, "Spatial.PluginSdk.dll"),
        ];
        foreach (var (source, name) in payload.Where(item => File.Exists(item.Source)))
        {
            File.Copy(source, Path.Combine(directory, name));
        }

        return directory;
    }

    /// <summary>
    /// Derives the manifest from the provider's contract descriptors so the
    /// package surface can never drift from the contracts it implements.
    /// </summary>
    private static PluginManifest BuildManifest(
        Spatial.PluginSdk.Capabilities.ICapabilityProvider provider,
        string assemblyType,
        string displayName)
    {
        var capabilities = provider.Descriptors.Select(ToManifestCapability).ToArray();
        return new PluginManifest(
            PluginManifest.SchemaVersionV1,
            provider.Id.ToString(),
            displayName,
            "dotnet",
            provider.GetType().Assembly.GetName().Name + ".dll",
            assemblyType,
            capabilities);
    }

    private static ManifestCapability ToManifestCapability(Spatial.PluginSdk.Capabilities.CapabilityDescriptor descriptor) =>
        new(
            descriptor.Id.ToString(),
            descriptor.Purpose,
            descriptor.Input.Name,
            descriptor.Output.Name,
            descriptor.Errors.Select(error => new ManifestError(error.Code, error.Description)).ToArray(),
            descriptor.RequiredPermissions.Select(permission => permission.Name).ToArray(),
            ManifestTraitMap.ToNames(descriptor.Traits).ToArray(),
            descriptor.Examples.Select(example => new ManifestExample(example.Name, example.Description)).ToArray());

    private static string ToJson(PluginManifest manifest)
    {
        var document = new
        {
            schemaVersion = manifest.SchemaVersion,
            id = manifest.Id,
            displayName = manifest.DisplayName,
            runtime = manifest.Runtime,
            assembly = manifest.Assembly,
            assemblyType = manifest.AssemblyType,
            capabilities = manifest.Capabilities.Select(ToJson).ToArray(),
        };
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    private static object ToJson(ManifestCapability capability) => new
    {
        id = capability.Id,
        purpose = capability.Purpose,
        inputSchema = capability.InputSchema,
        outputSchema = capability.OutputSchema,
        errors = capability.Errors.Select(error => new { code = error.Code, description = error.Description }).ToArray(),
        permissions = capability.Permissions,
        traits = capability.Traits,
        examples = capability.Examples.Select(example => new { name = example.Name, description = example.Description }).ToArray(),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
