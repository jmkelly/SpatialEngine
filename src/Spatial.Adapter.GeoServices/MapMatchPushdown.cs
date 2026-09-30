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
///     Find pushes the search text itself, as one folded pattern per searched
///     string field (ADR-0132): <c>%text%</c> for a contains search and
///     <c>text%</c> for a startsWith one, over the vocabulary's case-folding
///     text comparison — the one text comparison whose case behaviour every
///     back end states the same way, so the plan is a restriction rather than
///     an approximation of one. It used to be able to push only "a searched
///     field is not null", because the vocabulary's <c>LIKE</c> is
///     case-sensitive on some back ends and case-insensitive on others while
///     the served search is case-insensitive on all of them, so a pushed
///     <c>LIKE</c> would have dropped rows the adapter has to match (a subset,
///     where a pushdown has to be a superset). The adapter's own
///     case-insensitive match stays the answer.
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
    /// The plan the find search becomes: one folded pattern per searched string
    /// field, so a row can only match when the text is in one of those fields.
    /// The adapter's case-insensitive comparison of the text against that value
    /// is still what decides the row — the plan is a pre-filter, never the
    /// answer.
    /// </summary>
    /// <param name="fields">The string fields the search reads, in schema order.</param>
    /// <param name="searchText">The text the request searches for.</param>
    /// <param name="contains">Whether the search contains the text or starts with it.</param>
    public static FeatureQuery Search(IReadOnlyList<string> fields, string searchText, bool contains)
    {
        var pattern = Pattern(searchText, contains);
        if (pattern is null)
        {
            // The served search folds case in Unicode and the vocabulary's
            // folded comparison folds the ASCII alphabet — the one fold every
            // back end states identically (ADR-0132). A search text outside
            // that alphabet is therefore not pushed as a pattern, and the plan
            // falls back to the widest restriction it can state soundly: a row
            // can only match when at least one searched field carries a value.
            return new FeatureQuery(Where: new Predicate.Some(
                [.. fields.Select(field => new Predicate.IsNull(new FieldRef(field), Negated: true))]));
        }

        return new FeatureQuery(Where: new Predicate.Some(
            [.. fields.Select(field => new Predicate.Compare(
                new FieldRef(field),
                ComparisonOperator.LikeFolded,
                Literal.FromText(pattern)))]));
    }

    /// <summary>
    /// The pushed pattern for a search text, or null when the text carries a
    /// character the pattern cannot state the same way on every provider.
    /// <para>
    /// Two do. A character outside the ASCII alphabet is outside the fold the
    /// comparison applies, and a backslash is Postgres's <c>LIKE</c> escape
    /// character and nothing at all in T-SQL — a search text ending in one is a
    /// statement Postgres refuses (<c>LIKE pattern must not end with escape
    /// character</c>) and a pattern T-SQL reads as two characters. The plan is
    /// one tree for every store, so a text that either provider would read
    /// differently is not pushed and the caller falls back to the widest
    /// restriction that needs no reading.
    /// </para>
    /// </summary>
    private static string? Pattern(string searchText, bool contains) =>
        searchText.Any(character => character > 0x7f || character == '\\')
            ? null
            : contains ? $"%{searchText}%" : $"{searchText}%";

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
