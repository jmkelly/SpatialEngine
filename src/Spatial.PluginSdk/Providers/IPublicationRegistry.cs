namespace Spatial.PluginSdk.Providers;

/// <summary>
/// The protocol surface a <see cref="Publication"/> is exposed on
/// (ADR-0041). The kind selects which adapter projects the publication's
/// datasets: <see cref="Feature"/> is the Esri GeoServices <c>FeatureServer</c>
/// (ADR-0035), while <see cref="Map"/> and <see cref="Image"/> are the
/// planned MapServer / ImageServer projections
/// (<c>map-service-plan.md</c>, <c>image-service-plan.md</c>).
/// </summary>
public enum PublicationKind
{
    /// <summary>A feature service: queryable and, where the store supports it, editable layers.</summary>
    Feature,

    /// <summary>A map service: a read-only, ordered set of renderable layers.</summary>
    Map,

    /// <summary>An image service: a read-only raster or raster-catalog source.</summary>
    Image,
}

/// <summary>
/// One dataset projected as a layer of a <see cref="Publication"/>. The
/// <see cref="Dataset"/> is the engine dataset identifier
/// (<c>schema.table</c>) and <see cref="Name"/> optionally overrides the
/// published layer name. <see cref="LayerId"/> is assigned once and
/// persisted: ids are append-only, never reused and never renumbered, so
/// adding or removing a layer cannot renumber the others (ADR-0041).
///
/// <para><see cref="Style"/> is the layer's persisted draw recipe in the
/// engine's MapLibre style dialect (ADR-0044/ADR-0047): a JSON array of
/// style-layer objects carrying <c>type</c>/<c>layout</c>/<c>paint</c> (and
/// optionally <c>filter</c>/<c>minzoom</c>/<c>maxzoom</c>) only. It never
/// repeats the dataset or an id — the host injects <c>id</c> and
/// <c>source-layer: <see cref="Dataset"/></c> when it assembles a render
/// document. <see langword="null"/> or empty means the renderer's default
/// style.</para>
/// </summary>
public sealed record PublicationLayer(string Dataset, int LayerId, string? Name = null, string? Style = null)
{
    public override string ToString() => $"{LayerId}: {Dataset}";
}

/// <summary>
/// A named, ordered projection of datasets from one keyed engine store onto
/// a protocol surface (ADR-0041). It is the engine's neutral unit of service
/// exposure and carries no protocol or provider type, so an adapter maps a
/// publication to its wire shape and the registry implementation never sees
/// a protocol concept. Declared (configuration-seeded) publications are
/// immutable through the API; runtime publications are persisted by the
/// registry.
/// </summary>
public sealed record Publication(
    string Name,
    PublicationKind Kind,
    string Store,
    IReadOnlyList<PublicationLayer> Layers,
    string? Description = null,
    string? Copyright = null)
{
    public override string ToString() => $"{Name} ({Kind}, {Store}, {Layers.Count} layer(s))";
}

/// <summary>
/// Read/write access to the engine's publications (ADR-0041), replacing the
/// static, config-only service map the GeoServices adapter started with. The
/// registry is the single source the serving adapters resolve a service name
/// through, so a service created at runtime is immediately visible without a
/// restart. It is core-typed only; persistence lives in the implementation.
/// A missing name is <c>not.found</c>, a name collision with a declared
/// entry or an invalid publication is <c>invalid.arguments</c>.
/// </summary>
public interface IPublicationRegistry
{
    /// <summary>Returns every publication: declared entries first, then runtime entries by name.</summary>
    Task<IReadOnlyList<Publication>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns one publication by name, or a <c>not.found</c> failure.</summary>
    Task<Publication> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or replaces a runtime publication and returns the stored value
    /// (the registry may normalise layer ids). A name owned by a declared
    /// entry is <c>invalid.arguments</c>, not an overwrite.
    /// </summary>
    Task<Publication> PutAsync(Publication publication, CancellationToken cancellationToken = default);

    /// <summary>Deletes a runtime publication; returns whether it existed. A declared entry is not deletable.</summary>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default);
}
