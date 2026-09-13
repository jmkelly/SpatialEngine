namespace Spatial.PluginSdk.Providers;

/// <summary>
/// A protocol surface a <see cref="Map"/> can expose (ADR-0053). A map
/// declares any subset: <see cref="Feature"/> and <see cref="Map"/> are the
/// Esri GeoServices FeatureServer and MapServer (ADR-0035/ADR-0048),
/// <see cref="Tiles"/> is the neutral tile route over the render/tile
/// contracts (ADR-0046), <see cref="Wms"/> and <see cref="Wfs"/> are the OGC
/// boundary adapter, and <see cref="Image"/> is the GeoServices ImageServer
/// (ADR-0051). Each service is an independent projection of the same map.
/// </summary>
public enum MapService
{
    /// <summary>A feature service: queryable and, where the store supports it, editable feature layers.</summary>
    Feature,

    /// <summary>A map service: a read-only, ordered set of renderable feature layers.</summary>
    Map,

    /// <summary>A tile service: the map's composed style served as Web-Mercator XYZ tiles.</summary>
    Tiles,

    /// <summary>An OGC Web Map Service (WMS 1.3.0): capabilities, GetMap and GetFeatureInfo.</summary>
    Wms,

    /// <summary>An OGC Web Feature Service (WFS 2.0.0): capabilities, DescribeFeatureType and GetFeature.</summary>
    Wfs,

    /// <summary>An image service: the map's raster layers exposed as a GeoServices ImageServer.</summary>
    Image,
}

/// <summary>
/// Whether a <see cref="MapLayer"/> names a vector feature dataset or a
/// raster dataset (ADR-0053). Feature layers feed Feature/Map/Tiles/WMS/WFS;
/// image layers feed the Image service.
/// </summary>
public enum MapLayerKind
{
    /// <summary>A vector dataset served through the feature-query engine.</summary>
    Feature,

    /// <summary>A raster dataset served through the raster catalogue (ADR-0051).</summary>
    Image,
}

/// <summary>
/// One dataset projected as a layer of a <see cref="Map"/>. The
/// <see cref="Dataset"/> is the engine dataset identifier (for a feature
/// layer <c>schema.table</c>; for an image layer the raster dataset name) and
/// <see cref="Name"/> optionally overrides the published layer name.
/// <see cref="LayerId"/> is assigned once and persisted: ids are append-only,
/// never reused and never renumbered, so adding or removing a layer cannot
/// renumber the others (ADR-0041/ADR-0053).
///
/// <para><see cref="Style"/> is the layer's persisted draw recipe in the
/// engine's MapLibre style dialect (ADR-0044/ADR-0047): a JSON array of
/// style-layer objects carrying <c>type</c>/<c>layout</c>/<c>paint</c> (and
/// optionally <c>filter</c>/<c>minzoom</c>/<c>maxzoom</c>) only. It never
/// repeats the dataset or an id — the host injects <c>id</c> and
/// <c>source-layer: <see cref="Dataset"/></c> when it assembles a render
/// document. <see langword="null"/> or empty means the renderer's default
/// style. Visibility is carried by the fragment's <c>layout.visibility</c>,
/// so there is no separate flag.</para>
///
/// <para><see cref="Kind"/> selects the services the layer feeds.
/// <see cref="Store"/> optionally overrides the map's store for this layer,
/// so one map can mix a vector store and the keyed <c>raster</c> store.</para>
/// </summary>
public sealed record MapLayer(
    string Dataset,
    int LayerId,
    string? Name = null,
    string? Style = null,
    MapLayerKind Kind = MapLayerKind.Feature,
    string? Store = null)
{
    public override string ToString() => $"{LayerId}: {Dataset}";
}

/// <summary>
/// The engine's neutral unit of authoring and exposure (ADR-0053): a named,
/// ordered set of styled layers from one keyed store, plus the set of
/// services the map exposes. It carries no protocol or provider type, so an
/// adapter maps a map to its wire shape and the registry implementation never
/// sees a protocol concept. Declared (configuration-seeded) maps are
/// immutable through the API; runtime maps are persisted by the registry.
///
/// <para>A layer is owned by the map, so the same dataset can appear in
/// different maps with different styles; styles are never global. A map's
/// <see cref="Services"/> may be empty (a draft that serves nothing) or any
/// subset of <see cref="MapService"/>.</para>
/// </summary>
public sealed record Map(
    string Name,
    string Store,
    IReadOnlyList<MapLayer> Layers,
    IReadOnlyList<MapService> Services,
    string? Description = null,
    string? Copyright = null)
{
    public override string ToString() =>
        $"{Name} ({Store}, {Layers.Count} layer(s), {string.Join("/", Services)})";

    /// <summary>Whether the map exposes <paramref name="service"/>.</summary>
    public bool Exposes(MapService service) => Services.Contains(service);
}

/// <summary>
/// Read/write access to the engine's maps (ADR-0053), replacing the
/// per-protocol publication registry (ADR-0041). The registry is the single
/// source every serving adapter resolves a service name through, so a map
/// created at runtime is immediately visible without a restart. It is
/// core-typed only; persistence lives in the implementation. A missing name
/// is <c>not.found</c>, a name collision with a declared entry or an invalid
/// map is <c>invalid.arguments</c>.
/// </summary>
public interface IMapRegistry
{
    /// <summary>Returns every map: declared entries first, then runtime entries by name.</summary>
    Task<IReadOnlyList<Map>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns one map by name, or a <c>not.found</c> failure.</summary>
    Task<Map> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces a runtime map and returns the stored value (the
    /// registry may normalise layer ids). A name owned by a declared entry is
    /// <c>invalid.arguments</c>, not an overwrite.
    /// </summary>
    Task<Map> PutAsync(Map map, CancellationToken cancellationToken = default);

    /// <summary>Deletes a runtime map; returns whether it existed. A declared entry is not deletable.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);
}
