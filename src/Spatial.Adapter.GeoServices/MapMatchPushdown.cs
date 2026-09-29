using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer match surfaces compiled onto the store's query surface
/// (ADR-0112): the identify query envelope becomes the plan's bounding-box
/// pre-filter, and the find text search becomes the plan's attribute
/// predicate — so neither reads the whole table to reject rows in the adapter.
///
/// <para>
/// Both are decided before the store is asked and are all-or-nothing: a plan
/// is compiled only when the request is expressible, and a request that is not
/// keeps the whole-dataset read. There is no "try it, then scan".
/// </para>
///
/// <para>
/// What is pushed is a <em>pre-filter</em>, never the answer. It selects a
/// superset of the matching features and the in-memory matcher still decides
/// each of them (ADR-0074 §4), which is what makes this a refactor rather
/// than a behaviour change.
/// </para>
///
/// <para>
/// The two compilations differ in what a superset is allowed to mean, and
/// each is judged by whether the pushed restriction can only ever admit more.
/// <list type="bullet">
///   <item>
///     Identify pushes the query geometry's envelope, which admits everything
///     an intersection with that geometry can accept — a hit intersects the
///     query, so its envelope intersects the query's. It is only expressible
///     on a layer whose <c>OBJECTID</c> is store-derived: the identify filters
///     resolve the synthetic <c>OBJECTID</c> of a <c>layerDefs</c> clause
///     against the scan ordinal, so a read that returned only the boxed rows
///     would renumber that key (ADR-0097). The clause and the temporal
///     selection stay in the adapter, which is also what keeps the
///     <c>layerDefs</c> and <c>dynamicLayers</c> paths exactly as they were.
///   </item>
///   <item>
///     Find pushes the one restriction its vocabulary can state soundly: a row
///     can only match when at least one of the searched string fields carries
///     a value, so the plan is that disjunction of null tests. The text itself
///     is deliberately <em>not</em> pushed. The predicate vocabulary's only
///     text comparison is <c>LIKE</c>, and <c>LIKE</c> is case-sensitive on
///     some back ends and case-insensitive on others while the served search
///     is case-insensitive on all of them — so a pushed <c>LIKE</c> would drop
///     rows the adapter has to match (a subset, where a pushdown has to be a
///     superset), and the answer would depend on the store's collation. The
///     adapter's own case-insensitive match stays the answer.
///   </item>
/// </list>
/// </para>
/// </summary>
internal static class MapMatchPushdown
{
    /// <summary>
    /// The plan the identify envelope becomes, or <c>null</c> when this layer
    /// is not pushable and the caller reads the whole dataset.
    /// </summary>
    /// <param name="dataset">The layer's catalogue description.</param>
    /// <param name="queryGeometry">
    /// The identify geometry already reprojected into the layer's own CRS
    /// (ADR-0020's canonical interchange and the pre-transform the identify
    /// path does), so the box the store is asked for and the box the matcher
    /// tests are the same four numbers.
    /// </param>
    public static FeatureQuery? Identify(DatasetDescription dataset, IGeometry queryGeometry) =>
        Pushable(dataset) && queryGeometry.Envelope is { } envelope
            ? new FeatureQuery(BoundingBox: new Spatial.Contracts.BoundingBox(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY))
            : null;

    /// <summary>
    /// The plan the find search becomes: at least one of the searched string
    /// fields must carry a value. That is the widest restriction the plan can
    /// state without changing the answer — the adapter's match reads a value
    /// out of one of those fields and skips a row that has none — and the
    /// adapter's case-insensitive comparison of the text against that value is
    /// what decides the row.
    /// </summary>
    /// <param name="fields">The string fields the search reads, in schema order.</param>
    public static FeatureQuery Search(IReadOnlyList<string> fields) =>
        new(Where: new Predicate.Some(
            [.. fields.Select(field => new Predicate.IsNull(new FieldRef(field), Negated: true))]));

    /// <summary>
    /// Whether a layer's read can be restricted at all. It is a question about
    /// identity, never about dialect support: a layer whose <c>OBJECTID</c> is
    /// the scan ordinal would have that key renumbered by a read that returned
    /// only the matching rows (ADR-0097), and its object-id-carrying filters
    /// are numbered against that same ordinal.
    /// </summary>
    private static bool Pushable(DatasetDescription dataset) =>
        EsriObjectIdScheme.For(dataset).IsIdentity && dataset.IdColumns is [var _];
}
