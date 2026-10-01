using Spatial.Contracts.Providers;

namespace Spatial.Maps;

/// <summary>
/// Host configuration for the map registry (ADR-0053 §2):
/// <c>Spatial:Maps:Path</c> is the runtime JSON file,
/// <c>Spatial:Maps:LegacyPath</c> is an optional pre-ADR-0053 legacy map file
/// (<c>publications.json</c>) read once and migrated, and
/// <c>Spatial:Maps:Declared</c> seeds immutable, configuration-owned maps
/// (including the legacy <c>Spatial:GeoServices:Services</c> entries projected
/// to Feature-only maps).
/// </summary>
public sealed class MapsOptions
{
    /// <summary>The runtime map file; relative to the host's working directory.</summary>
    public string Path { get; set; } = "./data/maps.json";

    /// <summary>
    /// A pre-ADR-0053 legacy map file to migrate on first read. Empty
    /// disables migration; the legacy file is never rewritten.
    /// </summary>
    public string? LegacyPath { get; set; }

    /// <summary>Configuration-owned maps, immutable through the API.</summary>
    public IReadOnlyList<DeclaredMapOptions> Declared { get; set; } = [];
}

/// <summary>
/// One declared map. When <see cref="Layers"/> is empty the map exposes the
/// whole store: its layers are enumerated from the store's dataset list in
/// sorted order and their ids are assigned once and cached, so a legacy
/// whole-store configuration keeps working with stable ids.
/// </summary>
public sealed class DeclaredMapOptions
{
    public string Name { get; set; } = string.Empty;

    public string Store { get; set; } = string.Empty;

    /// <summary>The exposed services: any of <c>FeatureServer</c>, <c>MapServer</c>, <c>Tiles</c>, <c>Wms</c>, <c>Wfs</c>, <c>ImageServer</c>.</summary>
    public IReadOnlyList<string> Services { get; set; } = [];

    public string? Description { get; set; }

    public string? Copyright { get; set; }

    /// <summary>
    /// The authored service-level metadata document (ISO/FGDC XML) served by
    /// the ImageServer <c>metadata</c> resource (ADR-0068). Null means the
    /// service has no authored metadata and its resource answers <c>not.found</c>.
    /// </summary>
    public string? MetadataXml { get; set; }

    /// <summary>Explicit layers; empty means "every dataset in the store".</summary>
    public IReadOnlyList<DeclaredLayerOptions> Layers { get; set; } = [];
}

/// <summary>One explicit layer of a declared map.</summary>
public sealed class DeclaredLayerOptions
{
    public string Dataset { get; set; } = string.Empty;

    public int LayerId { get; set; }

    public string? Name { get; set; }

    /// <summary>
    /// The layer's MapLibre style dialect array as JSON (ADR-0047), or null
    /// for the renderer's default style. Validated when the registry seeds it.
    /// </summary>
    public string? Style { get; set; }

    /// <summary><c>Feature</c> (default) or <c>Image</c> (ADR-0053).</summary>
    public string Kind { get; set; } = nameof(MapLayerKind.Feature);

    /// <summary>Optional per-layer store override; empty uses the map store.</summary>
    public string? Store { get; set; }

    /// <summary>
    /// The relationships this layer declares to the map's other layers
    /// (ADR-0077); empty means none. Validated structurally when the
    /// registry seeds the map.
    /// </summary>
    public IReadOnlyList<DeclaredRelationshipOptions> Relationships { get; set; } = [];

    /// <summary>
    /// The date field holding each row's start instant, and the one holding its
    /// end (ADR-0183): the designation of this layer's feature temporal
    /// extent. Either may be absent; both absent designates nothing, and a
    /// layer that designates nothing is served by ADR-0100's typed refusals.
    /// Validated structurally when the registry seeds the map.
    /// </summary>
    public string? StartDateField { get; set; }

    /// <summary>The date field holding each row's end instant, or absent.</summary>
    public string? EndDateField { get; set; }
}

/// <summary>One declared relationship of a declared map layer (ADR-0077).</summary>
public sealed class DeclaredRelationshipOptions
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The layer id of the related layer, which the map must publish.</summary>
    public int RelatedLayerId { get; set; }

    /// <summary>The key column on this layer.</summary>
    public string PrimaryKeyColumn { get; set; } = string.Empty;

    /// <summary>The key column on the related layer.</summary>
    public string RelatedKeyColumn { get; set; } = string.Empty;

    /// <summary><c>OneToOne</c>, <c>OneToMany</c> (default) or <c>ManyToMany</c>.</summary>
    public string Cardinality { get; set; } = nameof(LayerRelationshipCardinality.OneToMany);

    /// <summary>Optional display title clients show for the related records.</summary>
    public string? TitleField { get; set; }

    /// <summary>The join dataset of a many-to-many relationship; required there and rejected elsewhere.</summary>
    public DeclaredRelationshipJoinOptions? Join { get; set; }
}

/// <summary>The join dataset of a declared many-to-many relationship (ADR-0077).</summary>
public sealed class DeclaredRelationshipJoinOptions
{
    public string Dataset { get; set; } = string.Empty;

    /// <summary>The join column carrying the origin layer's key.</summary>
    public string PrimaryKeyColumn { get; set; } = string.Empty;

    /// <summary>The join column carrying the related layer's key.</summary>
    public string RelatedKeyColumn { get; set; } = string.Empty;
}
