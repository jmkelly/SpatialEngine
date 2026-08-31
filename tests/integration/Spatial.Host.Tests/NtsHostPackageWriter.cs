using System.Text.Json;
using Spatial.Operations.NetTopologySuite;
using Spatial.PluginHost.DotNet.Manifest;

namespace Spatial.Host.Tests;

/// <summary>
/// Writes an immutable NetTopologySuite plugin package for the host's
/// supervisor: the manifest plus the provider assembly, its contracts and
/// NetTopologySuite itself (the worker host loads NTS from the package
/// directory because the host process does not reference it). Mirrors the
/// conformance suite's package writer so the host integration test proves the
/// full isolated-worker path over HTTP.
/// </summary>
internal static class NtsHostPackageWriter
{
    public static string Write(string root)
    {
        var manifest = BuildManifest();
        var directory = Path.Combine(root, manifest.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, PluginManifest.FileName), ToJson(manifest));

        var outputDirectory = Path.GetDirectoryName(typeof(NtsOperationsProvider).Assembly.Location)!;
        var ntsDirectory = Path.GetDirectoryName(typeof(NetTopologySuite.Geometries.Geometry).Assembly.Location)!;
        foreach (var (source, name) in new[]
        {
            (typeof(NtsOperationsProvider).Assembly.Location, "Spatial.Operations.NetTopologySuite.dll"),
            (Path.Combine(ntsDirectory, "NetTopologySuite.dll"), "NetTopologySuite.dll"),
            (typeof(Spatial.Core.Geometry.IGeometry).Assembly.Location, "Spatial.Core.dll"),
            (typeof(Spatial.PluginSdk.Capabilities.ICapabilityProvider).Assembly.Location, "Spatial.PluginSdk.dll"),
        })
        {
            File.Copy(source, Path.Combine(directory, name));
        }

        return directory;
    }

    /// <summary>
    /// Derives the manifest from the provider's contract descriptors so the
    /// package surface can never drift from the contracts it implements.
    /// </summary>
    private static PluginManifest BuildManifest()
    {
        var provider = new NtsOperationsProvider();
        var capabilities = provider.Descriptors.Select(ToManifestCapability).ToArray();
        return new PluginManifest(
            PluginManifest.SchemaVersionV1,
            provider.Id.ToString(),
            "NetTopologySuite operations",
            "dotnet",
            "Spatial.Operations.NetTopologySuite.dll",
            "Spatial.Operations.NetTopologySuite.NtsOperationsProvider",
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
            []);

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