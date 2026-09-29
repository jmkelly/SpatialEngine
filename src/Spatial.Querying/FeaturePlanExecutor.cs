using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Querying;

/// <summary>
/// The reference semantics of a feature-read plan (ADR-0074 §4): evaluate the
/// plan over the whole dataset, in one defined order, and return the page it
/// names. Every store's pushdown is measured against this — the conformance
/// suite runs a store's own SQL answer and this answer side by side — so the
/// reference is deliberately total and deliberately boring: it materialises
/// what a pushdown would avoid, which is exactly what makes it the definition
/// of correct.
///
/// <para>
/// The pipeline is the one a store must reproduce: select (identities, the
/// attribute predicate, then the bounding-box pre-filter), order (the
/// requested keys, then the feature identity as the contract's mandatory
/// tie-break), page (cursor or offset, then the cap), project (last, so the
/// ordering and the page size are decided over whole features).
/// </para>
///
/// <para>
/// The predicate is evaluated by <see cref="ReferencePredicate"/>, the one
/// reference evaluator, so a plan run here and a plan pushed down to SQL are
/// answering the same question with the same rules. A store that already
/// applied the restriction in its dialect hands its selected rows to
/// <see cref="Finish"/> instead, which is the shaping half on its own.
/// </para>
/// </summary>
public static class FeaturePlanExecutor
{
    /// <summary>How many features a batch carries (the engine's canonical batch size).</summary>
    public const int BatchSize = 512;

    /// <summary>
    /// Evaluates a plan over the given features. The features are the
    /// dataset's, in the store's own order; the plan is validated against the
    /// schema first, so a plan the schema does not admit fails here with
    /// <c>invalid.arguments</c> rather than returning something plausible.
    /// </summary>
    public static FeatureQueryPage Execute(
        IFeatureSchema schema,
        IReadOnlyList<Feature> features,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(features);
        FeatureQueryValidation.Validate(schema, query);

        var selected = Select(schema, features, query, cancellationToken);
        return Finish(schema, selected, query, cancellationToken);
    }

    /// <summary>
    /// The shaping half of the plan — order, page, project — over rows that
    /// have <em>already</em> been selected, by a store's dialect or by
    /// <see cref="Select"/>. A store that pushed the restriction down calls
    /// this rather than <see cref="Execute"/>, because re-selecting rows a
    /// dialect already filtered would be wasted work and, where the store has
    /// no in-memory evaluator, impossible. The page's total is the selected
    /// count: these rows are everything the plan matched, so exhaustion is
    /// known from the page itself and never needs a second count (ADR-0116 §2).
    ///
    /// <para>
    /// The plan is still validated against the schema on the way in: a store
    /// that only finishes the plan must reject a plan it could not express as
    /// firmly as a store that selected it, or a bad projection would be a
    /// different answer per provider.
    /// </para>
    /// </summary>
    public static FeatureQueryPage Finish(
        IFeatureSchema schema,
        IReadOnlyList<Feature> selected,
        FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);
        FeatureQueryValidation.Validate(schema, query);
        var ordered = Order(schema, selected.ToList(), query);
        var start = FeaturePageCursor.StartOffset(query);
        var page = Page(ordered, start, query.Limit, out var consumed, out var hasMore);
        var projected = Project(schema, page, query.Projection, cancellationToken);
        var batches = Batches(projected.Schema, projected.Features);
        var cursor = hasMore
            ? FeaturePageCursor.Issue(query, start + consumed)
            : null;
        return new FeatureQueryPage(batches, cursor, selected.Count, hasMore);
    }

    /// <summary>
    /// The features a plan selects, in store order: the identity restriction,
    /// then the attribute predicate, then the bounding-box pre-filter. The
    /// pre-filter is the plan's only spatial component (ADR-0074 §8: the
    /// topology verbs are the caller's verb, evaluated over the rows a store
    /// fetched). A store that cannot push a plan down evaluates it here over
    /// the rows it read.
    /// </summary>
    public static List<Feature> Select(
        IFeatureSchema schema,
        IReadOnlyList<Feature> features,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        var wanted = query.Ids is null ? null : new HashSet<FeatureId>(query.Ids);
        var restrictive = wanted is not null || query.Where is not null || query.BoundingBox is not null;
        var selected = new List<Feature>(restrictive ? 0 : features.Count);
        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (wanted is not null && !wanted.Contains(feature.Id))
            {
                continue;
            }

            if (query.Where is { } predicate && !ReferencePredicate.Matches(predicate, feature))
            {
                continue;
            }

            if (query.BoundingBox is { } bbox && !Intersects(schema, feature, bbox))
            {
                continue;
            }

            selected.Add(feature);
        }

        return selected;
    }

    /// <summary>
    /// The bounding-box pre-filter: a feature matches when its geometry's
    /// envelope meets the box, and a feature with no geometry (or no envelope)
    /// never does. The comparison is the ordinary closed-interval overlap, so
    /// a feature touching the box boundary matches.
    /// </summary>
    private static bool Intersects(IFeatureSchema schema, Feature feature, BoundingBox bbox)
    {
        for (var i = 0; i < schema.Count; i++)
        {
            if (schema[i].Kind == AttributeKind.Geometry
                && !feature[i].IsNull
                && feature[i].GeometryValue.Envelope is { } envelope
                && envelope.MinX <= bbox.MaxX
                && envelope.MaxX >= bbox.MinX
                && envelope.MinY <= bbox.MaxY
                && envelope.MaxY >= bbox.MinY)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Applies the plan's sort keys, then the mandatory feature-identity
    /// tie-break, so the total order is deterministic: two features whose
    /// requested keys tie are always separated, and a page boundary can never
    /// fall between two rows the next page would re-order. With no requested
    /// order the store's own order is the plan's order.
    /// </summary>
    private static List<Feature> Order(IFeatureSchema schema, List<Feature> features, FeatureQuery query)
    {
        if (query.Order is not { Count: > 0 } order)
        {
            return features;
        }

        var keys = order.Select(term => Key(schema, term)).ToArray();
        IOrderedEnumerable<Feature>? ordered = null;
        foreach (var key in keys)
        {
            var index = key.Index;
            var selector = new Func<Feature, AttributeValue>(feature => feature[index]);
            ordered = ordered is null
                ? Sort(features, selector, key.Term)
                : Sort(ordered, selector, key.Term);
        }

        return ThenSort(ordered!, Identity, descending: false).ToList();
    }

    private static Func<Feature, AttributeValue> Identity => feature => AttributeValue.FromString(feature.Id.Value);

    private static IOrderedEnumerable<Feature> Sort(
        IEnumerable<Feature> features, Func<Feature, AttributeValue> selector, OrderTerm term) =>
        term.IsDescending
            ? features.OrderByDescending(selector, AttributeValueComparer.Instance)
            : features.OrderBy(selector, AttributeValueComparer.Instance);

    /// <summary>
    /// Appends one key to an existing order. It has to be a <em>then</em>-key:
    /// an <c>OrderBy</c> here would discard the keys already applied and the
    /// tie-break would become the only order.
    /// </summary>
    private static IOrderedEnumerable<Feature> ThenSort(
        IOrderedEnumerable<Feature> ordered, Func<Feature, AttributeValue> selector, bool descending) =>
        descending
            ? ordered.ThenByDescending(selector, AttributeValueComparer.Instance)
            : ordered.ThenBy(selector, AttributeValueComparer.Instance);

    private static SortKey Key(IFeatureSchema schema, OrderTerm term) =>
        new(schema.IndexOf(term.Field), term);

    private readonly record struct SortKey(int Index, OrderTerm Term);

    /// <summary>
    /// The page: the rows from the page start, capped. Reports how many rows
    /// the page consumed and whether more remain, which is what decides
    /// whether a continuation cursor is issued.
    /// </summary>
    private static List<Feature> Page(List<Feature> ordered, int start, int? limit, out int consumed, out bool hasMore)
    {
        var available = Math.Max(ordered.Count - start, 0);
        var taken = limit is { } cap ? Math.Min(cap, available) : available;
        consumed = taken;
        hasMore = start + taken < ordered.Count;
        return taken == 0 ? [] : ordered.GetRange(Math.Min(start, ordered.Count), taken);
    }

    /// <summary>
    /// Projects the page's features onto the requested fields, in the
    /// requested order, by building the reduced schema once and reading each
    /// feature through it. A feature's identity is never projected away: it is
    /// the feature's, not a field. The reduced schema is what the page's
    /// batches carry, so a reader never has to know which fields were dropped.
    /// </summary>
    private static ProjectedFeatures Project(
        IFeatureSchema schema, List<Feature> features, IReadOnlyList<string>? projection, CancellationToken cancellationToken)
    {
        if (projection is null || projection.Count == 0)
        {
            return new ProjectedFeatures((FeatureSchema)schema, features);
        }

        var indexes = projection
            .Where(field => field != AggregateSpec.AllFields)
            .Select(schema.IndexOf)
            .ToArray();
        var fields = indexes.Select(index => schema[index]).ToArray();
        var reduced = new FeatureSchema(fields);
        var projected = new List<Feature>(features.Count);
        foreach (var feature in features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new AttributeValue[indexes.Length];
            for (var i = 0; i < indexes.Length; i++)
            {
                values[i] = feature[indexes[i]];
            }

            projected.Add(new Feature(feature.Id, reduced, values));
        }

        return new ProjectedFeatures(reduced, projected);
    }

    /// <summary>
    /// Chunks the page into canonical batches, always emitting at least one
    /// (possibly empty) batch, so a caller that reads a page's first batch
    /// always has a schema to read it with.
    /// </summary>
    private static FeatureBatch[] Batches(FeatureSchema schema, List<Feature> features) =>
        features.Count == 0
            ? [new FeatureBatch(schema, [])]
            : features.Chunk(BatchSize).Select(chunk => new FeatureBatch(schema, chunk)).ToArray();

    private readonly record struct ProjectedFeatures(FeatureSchema Schema, List<Feature> Features);
}
