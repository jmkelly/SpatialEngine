using Spatial.PluginHost.DotNet.Manifest;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginHost.DotNet.Tests;

/// <summary>
/// The manifest fixtures the Phase 5 tests share: two versions of the
/// <c>fixture</c> plugin package (v1 with the fault fixtures, v2 a coexisting
/// generation) as validated <see cref="PluginManifest"/> records and as JSON
/// documents for writing real package directories.
/// </summary>
public static class FixtureManifest
{
    public const string AssemblyName = "Spatial.Plugin.Fixtures.dll";
    public const string V1ProviderType = "Spatial.Plugin.Fixtures.FixtureProviderV1";
    public const string V2ProviderType = "Spatial.Plugin.Fixtures.FixtureProviderV2";

    public static PluginManifest V1() =>
        new(
            PluginManifest.SchemaVersionV1,
            "fixture@1",
            "Fixture plugin v1",
            "dotnet",
            AssemblyName,
            V1ProviderType,
            V1Capabilities());

    public static PluginManifest V2() =>
        new(
            PluginManifest.SchemaVersionV1,
            "fixture@2",
            "Fixture plugin v2",
            "dotnet",
            AssemblyName,
            V2ProviderType,
            V2Capabilities());

    public static IReadOnlyList<ManifestCapability> V1Capabilities() =>
    [
        Capability("spatial.fixture.peek@1", "Returns the provider id that served the invocation.", "none", "scalar"),
        Capability("spatial.fixture.mint@1", "Mints a runtime-owned resource handle through the invocation facilities.", "none", "resource"),
        Capability("spatial.fixture.sleep@1", "Sleeps for the requested milliseconds, reporting progress — cancellable.", "scalar", "scalar", ["cancellable", "long-running"]),
        Capability("spatial.fixture.timeout@1", "Sleeps far beyond a caller deadline so timeout attribution can be observed.", "scalar", "scalar", ["cancellable", "long-running"]),
        Capability("spatial.fixture.crash@1", "Crashes the worker process immediately — the crash fault fixture.", "none", "scalar"),
        Capability("spatial.fixture.stream@1", "Streams string items through a bounded stream across the worker boundary.", "scalar", "stream.chunk", ["cancellable", "streaming"]),
    ];

    public static IReadOnlyList<ManifestCapability> V2Capabilities() =>
    [
        Capability("spatial.fixture.peek@1", "Returns the provider id that served the invocation.", "none", "scalar"),
        Capability("spatial.fixture.sleep@1", "Sleeps for the requested milliseconds, reporting progress — cancellable.", "scalar", "scalar", ["cancellable", "long-running"]),
    ];

    /// <summary>Builds a manifest capability with the standard error variant and no permissions or examples.</summary>
    public static ManifestCapability Capability(
        string id,
        string purpose,
        string input,
        string output,
        IReadOnlyList<string>? traits = null) =>
        new(
            id,
            purpose,
            input,
            output,
            [new ManifestError("invalid.arguments", "An argument is missing or of the wrong kind.")],
            [],
            traits ?? [],
            []);

    /// <summary>Serialises a manifest to the camelCase JSON document shape the loader accepts.</summary>
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
        return System.Text.Json.JsonSerializer.Serialize(document, JsonOptions);
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

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

    /// <summary>Builds the provider-side descriptor a worker would report for a manifest capability (compat tests).</summary>
    public static CapabilityDescriptor ToDescriptor(ManifestCapability capability) =>
        new(
            CapabilityId.Parse(capability.Id),
            capability.Purpose,
            new SchemaDescriptor(capability.InputSchema),
            new SchemaDescriptor(capability.OutputSchema),
            capability.Errors.Select(error => new ErrorVariant(error.Code, error.Description)).ToArray(),
            capability.Permissions.Select(Permission.Parse).ToArray(),
            ManifestTraitMap.FromNames(capability.Traits),
            capability.Examples.Select(example => new ConformanceExample(example.Name, example.Description)).ToArray());
}
