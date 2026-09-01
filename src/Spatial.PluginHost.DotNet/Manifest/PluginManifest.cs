namespace Spatial.PluginHost.DotNet.Manifest;

/// <summary>
/// The finalised plugin manifest schema (plan §16 Phase 5 "finalise manifest
/// schema"): the immutable declaration of one immutable plugin package — its
/// provider identity, runtime hint, loadable payload and the capability
/// contracts it serves (plan §9). The schema is language-neutral
/// (ADR-0013/0025); this record is the .NET view of the JSON document
/// described in <c>architecture/distilled/plugins.md</c>. Every field is
/// validated by <see cref="PluginManifestValidator"/> before a package may be
/// launched.
/// </summary>
public sealed record PluginManifest(
    int SchemaVersion,
    string Id,
    string DisplayName,
    string Runtime,
    string Assembly,
    string? AssemblyType,
    IReadOnlyList<ManifestCapability> Capabilities)
{
    /// <summary>The current manifest schema version; version 1 is the initial wire contract.</summary>
    public const int SchemaVersionV1 = 1;

    /// <summary>The JSON file every plugin package carries (schema v1).</summary>
    public const string FileName = "manifest.json";

    /// <summary>The provider id this package activates, for example <c>fixture@1</c>.</summary>
    public string ProviderId => Id;

    public override string ToString() => $"{Id} ({Runtime}, {Capabilities.Count} capabilities)";
}

/// <summary>
/// One capability contract a plugin package serves (plan §9): the versioned
/// capability id, its purpose, the names of both interchange schemas, the
/// declared error variants, the required permissions, the runtime traits and
/// the conformance examples. The manifest carries the contract *identity* the
/// supervisor validates against the loaded provider at activation — the
/// exact schema shapes and examples bodies live with the implementation.
/// </summary>
public sealed record ManifestCapability(
    string Id,
    string Purpose,
    string InputSchema,
    string OutputSchema,
    IReadOnlyList<ManifestError> Errors,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> Traits,
    IReadOnlyList<ManifestExample> Examples);

/// <summary>One declared failure mode (stable dotted code + when it occurs).</summary>
public sealed record ManifestError(string Code, string Description);

/// <summary>A named, described conformance example a conforming provider must serve identically.</summary>
public sealed record ManifestExample(string Name, string Description);
