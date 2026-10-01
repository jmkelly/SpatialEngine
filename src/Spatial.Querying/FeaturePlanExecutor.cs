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
        var start = FeaturePageCursor.StartOffset(query);

        // The order is taken over the page's window rather than over every row:
        // a whole read finished here (a keyless layer's plan, ADR-0097 §1) must
        // not sort 34,135 rows to name the 25 of them the page answers with
        // (SpatialEngine-yup).
        var ordered = Order(schema, selected, query, start, query.Limit);
        var page = Page(ordered, selected.Count, start, query.Limit, out var consumed, out var hasMore);
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
    /// Applies the plan's sort keys in the order the plan names them, each one
    /// a tie-break over the keys before it, then the mandatory feature-identity
    /// tie-break, so the total order is deterministic: two features whose
    /// requested keys tie are always separated, and a page boundary can never
    /// fall between two rows the next page would re-order. With no requested
    /// order the store's own order is the plan's order.
    ///
    /// <para>
    /// A capped plan is ordered only as far as its page reaches: the window is
    /// the page start plus the cap, and the <em>same</em> first
    /// <c>start + limit</c> rows the full order would name, selected without
    /// sorting the rows behind them. The order is total — the identity
    /// tie-break, and the read's own order behind it — so the window of the
    /// bounded selection and the window of the full sort are one answer, and
    /// which one a store reaches is a cost, never an answer
    /// (SpatialEngine-yup).
    /// </para>
    /// </summary>
    private static List<Feature> Order(
        IFeatureSchema schema, IReadOnlyList<Feature> features, FeatureQuery query, int start, int? limit)
    {
        if (query.Order is not { Count: > 0 } order)
        {
            return features as List<Feature> ?? features.ToList();
        }

        var comparer = new PlanOrder(schema, order);
        var window = limit is { } cap && start <= int.MaxValue - cap ? start + cap : int.MaxValue;
        if (window >= features.Count)
        {
            var all = new int[features.Count];
            for (var i = 0; i < all.Length; i++)
            {
                all[i] = i;
            }

            return ByIndex(all, features, comparer);
        }

        return Window(features, comparer, window);
    }

    /// <summary>
    /// The first <paramref name="window"/> rows of the plan's order, by a
    /// bounded selection: a max-heap of that size over the whole read, so the
    /// rows behind the page are compared but never held. Its memory is the
    /// page's window and its time is one comparison per row against the heap's
    /// root — a sort of the read is neither.
    /// </summary>
    private static List<Feature> Window(IReadOnlyList<Feature> features, PlanOrder order, int window)
    {
        var heap = new int[window];
        for (var index = 0; index < window; index++)
        {
            heap[index] = index;
        }

        for (var position = (window / 2) - 1; position >= 0; position--)
        {
            SiftDown(heap, features, order, position);
        }

        for (var index = window; index < features.Count; index++)
        {
            // The root is the largest row held; a row that beats it displaces it.
            if (Compares(features, order, index, heap[0]) >= 0)
            {
                continue;
            }

            heap[0] = index;
            SiftDown(heap, features, order, 0);
        }

        return ByIndex(heap, features, order);
    }

    /// <summary>The rows the given read positions hold, in the plan's order.</summary>
    private static List<Feature> ByIndex(int[] indexes, IReadOnlyList<Feature> features, PlanOrder order)
    {
        Array.Sort(indexes, (left, right) => Compares(features, order, left, right));
        var ordered = new List<Feature>(indexes.Length);
        foreach (var index in indexes)
        {
            ordered.Add(features[index]);
        }

        return ordered;
    }

    /// <summary>Restores the max-heap property over the row at <paramref name="position"/>.</summary>
    private static void SiftDown(int[] heap, IReadOnlyList<Feature> features, PlanOrder order, int position)
    {
        while (true)
        {
            var left = (2 * position) + 1;
            if (left >= heap.Length)
            {
                return;
            }

            var right = left + 1;
            var largest = right < heap.Length && Compares(features, order, heap[right], heap[left]) > 0 ? right : left;
            if (Compares(features, order, heap[position], heap[largest]) >= 0)
            {
                return;
            }

            (heap[position], heap[largest]) = (heap[largest], heap[position]);
            position = largest;
        }
    }

    /// <summary>
    /// Two rows of the read, in the plan's order and — when the order cannot
    /// separate them, which only two rows of one identity can — in the order
    /// the read returned them. That last key is what makes the order total, so
    /// a bounded selection and a full sort name the same window.
    /// </summary>
    private static int Compares(IReadOnlyList<Feature> features, PlanOrder order, int left, int right) =>
        order.Compare(features[left], features[right]) is var byOrder && byOrder != 0 ? byOrder : left.CompareTo(right);

    /// <summary>
    /// The plan's total order over whole features: every requested key in the
    /// order the plan names them, each a tie-break over the keys before it
    /// (ADR-0127), then the contract's mandatory feature-identity tie-break,
    /// then the order the read returned. That last key is what makes the order
    /// total even when a read names two features alike, so a bounded selection
    /// and a full sort cannot disagree about a window.
    /// </summary>
    private sealed class PlanOrder(IFeatureSchema schema, IReadOnlyList<OrderTerm> order)
    {
        private readonly Key[] _keys = [.. order.Select(term => new Key(schema.IndexOf(term.Field), term.IsDescending))];

        public int Compare(Feature left, Feature right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            foreach (var key in _keys)
            {
                var order = AttributeValueComparer.Instance.Compare(left[key.Index], right[key.Index]);
                if (order != 0)
                {
                    return key.Descending ? -order : order;
                }
            }

            return string.CompareOrdinal(left.Id.Value, right.Id.Value);
        }

        private readonly record struct Key(int Index, bool Descending);
    }

    /// <summary>
    /// The page: the rows from the page start, capped. Reports how many rows
    /// the page consumed and whether more remain, which is what decides
    /// whether a continuation cursor is issued. The rows come from
    /// <paramref name="ordered"/>, which holds the page's window and may be
    /// shorter than <paramref name="total"/> — the rows behind the window were
    /// compared and dropped, not read (SpatialEngine-yup) — so exhaustion is
    /// decided against the total, never against the window.
    /// </summary>
    private static List<Feature> Page(
        List<Feature> ordered, int total, int start, int? limit, out int consumed, out bool hasMore)
    {
        var available = Math.Max(total - start, 0);
        var taken = limit is { } cap ? Math.Min(cap, available) : available;
        consumed = taken;
        hasMore = start + taken < total;
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
