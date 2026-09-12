using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.Publications;

/// <summary>
/// Host configuration for the publication registry (ADR-0041 §2, §4.6):
/// <c>Spatial:Publications:Path</c> is the runtime JSON file and
/// <c>Spatial:Publications:Declared</c> seeds immutable, configuration-owned
/// publications (including the legacy <c>Spatial:GeoServices:Services</c>
/// entries projected to <see cref="PublicationKind.Feature"/>).
/// </summary>
public sealed class PublicationsOptions
{
    /// <summary>The runtime publication file; relative to the host's working directory.</summary>
    public string Path { get; set; } = "./data/publications.json";

    /// <summary>Configuration-owned publications, immutable through the API.</summary>
    public IReadOnlyList<DeclaredPublicationOptions> Declared { get; set; } = [];
}

/// <summary>
/// One declared publication. When <see cref="Layers"/> is empty the
/// publication exposes the whole store: its layers are enumerated from the
/// store's dataset list in sorted order and their ids are assigned once and
/// cached, so the legacy whole-store config keeps working with stable ids.
/// </summary>
public sealed class DeclaredPublicationOptions
{
    public string Name { get; set; } = string.Empty;

    public string Store { get; set; } = string.Empty;

    /// <summary><c>Feature</c> (default), <c>Map</c> or <c>Image</c>.</summary>
    public string Kind { get; set; } = nameof(PublicationKind.Feature);

    public string? Description { get; set; }

    public string? Copyright { get; set; }

    /// <summary>Explicit layers; empty means "every dataset in the store".</summary>
    public IReadOnlyList<DeclaredLayerOptions> Layers { get; set; } = [];
}

/// <summary>One explicit layer of a declared publication.</summary>
public sealed class DeclaredLayerOptions
{
    public string Dataset { get; set; } = string.Empty;

    public int LayerId { get; set; }

    public string? Name { get; set; }
}
