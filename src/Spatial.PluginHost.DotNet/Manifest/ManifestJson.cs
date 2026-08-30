namespace Spatial.PluginHost.DotNet.Manifest;

/// <summary>
/// The JSON document shape of <c>manifest.json</c> (schema v1) as parsed from
/// disk — camelCase property names, every list optional (an absent list means
/// an empty one). These DTOs exist so the loader can be lenient about missing
/// optional fields and surface *all* structural problems through the
/// validator instead of failing on the first null. Not a public contract;
/// the validated <see cref="PluginManifest"/> is.
/// </summary>
internal sealed class ManifestJson
{
    public int? SchemaVersion { get; set; }

    public string? Id { get; set; }

    public string? DisplayName { get; set; }

    public string? Runtime { get; set; }

    public string? Assembly { get; set; }

    public string? AssemblyType { get; set; }

    public List<ManifestCapabilityJson>? Capabilities { get; set; }
}

internal sealed class ManifestCapabilityJson
{
    public string? Id { get; set; }

    public string? Purpose { get; set; }

    public string? InputSchema { get; set; }

    public string? OutputSchema { get; set; }

    public List<ManifestErrorJson>? Errors { get; set; }

    public List<string>? Permissions { get; set; }

    public List<string>? Traits { get; set; }

    public List<ManifestExampleJson>? Examples { get; set; }
}

internal sealed class ManifestErrorJson
{
    public string? Code { get; set; }

    public string? Description { get; set; }
}

internal sealed class ManifestExampleJson
{
    public string? Name { get; set; }

    public string? Description { get; set; }
}
