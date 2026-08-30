using System.Text.Json;
using System.Text.Json.Serialization;
using Spatial.Operations.NetTopologySuite;
using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.Conformance.Tests;

/// <summary>
/// The plugin package manifest for the NetTopologySuite operations worker,
/// derived from the provider's contract descriptors so the package surface
/// can never drift from the contracts it implements (the worker host's
/// activation compatibility check then passes by construction; the Phase 5
/// fixture tests exercise the divergence gate deliberately).
/// </summary>
internal static class NtsManifest
{
    public const string AssemblyName = "Spatial.Operations.NetTopologySuite.dll";
    public const string ProviderType = "Spatial.Operations.NetTopologySuite.NtsOperationsProvider";

    public static PluginManifest Build()
    {
        var provider = new NtsOperationsProvider();
        var capabilities = provider.Descriptors.Select(ToManifest).ToArray();
        return new PluginManifest(
            PluginManifest.SchemaVersionV1,
            provider.Id.ToString(),
            "NetTopologySuite operations",
            "dotnet",
            AssemblyName,
            ProviderType,
            capabilities);
    }

    public static string ToJson(PluginManifest manifest)
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

    private static ManifestCapability ToManifest(CapabilityDescriptor descriptor) =>
        new(
            descriptor.Id.ToString(),
            descriptor.Purpose,
            descriptor.Input.Name,
            descriptor.Output.Name,
            descriptor.Errors.Select(error => new ManifestError(error.Code, error.Description)).ToArray(),
            descriptor.RequiredPermissions.Select(permission => permission.Name).ToArray(),
            ManifestTraitMap.ToNames(descriptor.Traits).ToArray(),
            descriptor.Examples.Select(example => new ManifestExample(example.Name, example.Description)).ToArray());

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
