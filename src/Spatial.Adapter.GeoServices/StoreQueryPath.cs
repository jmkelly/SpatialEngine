using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.Querying;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The served feature query compiled onto the store's query surface
/// (ADR-0098 §7): <c>outFields</c> becomes the projection,
/// <c>orderByFields</c> the store's ordering, <c>resultOffset</c> /
/// <c>resultRecordCount</c> the page start and cap, <c>returnCountOnly</c> a
/// count, <c>returnDistinctValues</c> a distinct set, and each reduction is
/// asked of the store's own face when it has one. What the adapter used to
/// count, deduplicate and group over a materialised match set is now the
/// store's answer.
///
/// <para>
/// The pushdown is honest about what it can express. It happens only when the
/// <em>whole</em> match is in the plan — no <c>time</c>, no <c>objectIds</c>,
/// no <c>uniqueIds</c>, and a geometry the box pre-filter answers
/// (<c>spatialRel=envelopeIntersects</c> or no geometry at all). The Esri where
/// grammar compiles to a predicate and rides along in the plan's
/// <c>Where</c> (ADR-0074 §7, SpatialEngine-u2x.8) — but only when
/// <see cref="EsriWhereResolver"/> says the clause is a pushdown, which is
/// exactly when the layer's <c>OBJECTID</c> is store-derived; a clause the
/// facade has to number rows against keeps the scan-and-match path, unchanged,
/// because a residual the plan cannot carry would page over the wrong row set.
/// The topology relations stay adapter-side (ADR-0074 §8).
/// </para>
///
/// <para>
/// The feature path additionally needs a layer whose <c>OBJECTID</c> is a durable
/// identity column (ADR-0037): a layer without one serves its object ids as scan
/// ordinals, which a paged store read does not have. The reduction paths need
/// no object id, so they push on any layer.
/// </para>
/// </summary>
internal static class StoreQueryPath
{
    /// <summary>
    /// Answers the query through the store, or returns <c>null</c> when this
    /// request has to take the scan-and-match path after all.
    /// </summary>
    public static async Task<IResult?> TryAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriFeatureQuery query,
        IGeometry? queryGeometry,
        EsriObjectIdScheme scheme,
        CoordinateReference? layerCrs,
        QueryServices services,
        CancellationToken cancellationToken)
    {
        // The clause a store can answer, or null when the facade has to evaluate
        // it per feature. A request that carries a clause and cannot push it
        // down is not pushable at all: the plan would page over every row
        // instead of the matching ones.
        var clause = EsriWhereResolver.Pushdown(query.Where, scheme, dataset);
        if ((query.Where is not null && clause is null) || !Pushable(query))
        {
            return null;
        }

        var plan = Plan(dataset, query, queryGeometry, clause);
        if (plan is null)
        {
            return null;
        }
        if (query.ReturnCountOnly)
        {
            return await CountAsync(dataset, store, plan, query, cancellationToken).ConfigureAwait(false);
        }

        if (query.ReturnExtentOnly || query.OutStatistics is not null)
        {
            // An extent spans the whole match set and a statistics response is
            // grouped over it; neither is a page. Both stay on the match path
            // until the reduction face carries the shape end to end.
            return null;
        }

        if (query.ReturnDistinctValues)
        {
            return await DistinctAsync(dataset, store, plan, query, layerCrs, cancellationToken).ConfigureAwait(false);
        }

        return scheme.IsIdentity
            ? await FeaturesAsync(dataset, store, plan, query, scheme, layerCrs, services, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// Whether every part of the match is in the plan. The where clause is
    /// resolved by the caller and is not re-checked here: it is either in the
    /// plan or the request is not pushable at all.
    /// </summary>
    private static bool Pushable(EsriFeatureQuery query) =>
        query.Time is null
        && query.ObjectIds is null
        && query.UniqueIds is null
        && !query.ReturnUniqueIdsOnly
        && (query.Geometry is null || string.Equals(query.SpatialRel, EsriFeatureQuery.EnvelopeIntersects, StringComparison.Ordinal));

    /// <summary>
    /// The plan the request becomes, or <c>null</c> when the request turns out
    /// not to be pushable after all: the where clause as the plan's predicate,
    /// the query geometry's envelope as the pre-filter, the projection from
    /// <c>outFields</c> (with the geometry field kept, because
    /// <c>returnGeometry</c> and <c>outSR</c> are the response's business, not
    /// the store's), the order from <c>orderByFields</c>, and the page start and
    /// cap.
    /// </summary>
    private static FeatureQuery? Plan(
        DatasetDescription dataset, EsriFeatureQuery query, IGeometry? queryGeometry, Predicate? clause)
    {
        if (!TryOrder(dataset, query, out var order))
        {
            return null;
        }

        var projection = Projection(dataset, query);
        var offset = Math.Min(FeaturePaging.ResolveOffset(query), int.MaxValue);
        return new FeatureQuery(
            Where: clause,
            BoundingBox: Envelope(queryGeometry),
            Projection: projection,
            Order: order,
            Limit: FeaturePaging.EffectivePageSize(query),
            Offset: offset);
    }

    private static BoundingBox? Envelope(IGeometry? geometry) =>
        geometry?.Envelope is { } envelope
            ? new BoundingBox(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY)
            : null;

    /// <summary>
    /// The projection the store is asked for: the projected attributes plus
    /// the two fields the response needs but <c>outFields</c> never names — the
    /// geometry (<c>returnGeometry</c> and <c>outSR</c> are the response's
    /// business) and the identity column (the <c>OBJECTID</c> the response
    /// reports, which the codec then leaves out of the attributes it writes).
    /// </summary>
    private static List<string>? Projection(DatasetDescription dataset, EsriFeatureQuery query)
    {
        if (query.OutFields is not { Count: > 0 } outFields)
        {
            return null;
        }

        var fields = new List<string>(outFields.Count + 2);
        foreach (var field in outFields)
        {
            if (dataset.Schema.IndexOf(field) < 0)
            {
                throw GeoServicesErrors.Invalid($"'outFields' names unknown field '{field}' in layer '{dataset.Id}'.");
            }

            fields.Add(field);
        }

        var geometry = FeatureGeometry.Index(dataset.Schema);
        if (geometry >= 0)
        {
            Add(fields, dataset.Schema[geometry].Name);
        }

        foreach (var column in dataset.IdColumns)
        {
            Add(fields, column);
        }

        return fields;

        static void Add(List<string> fields, string name)
        {
            if (!fields.Contains(name, StringComparer.Ordinal))
            {
                fields.Add(name);
            }
        }
    }

    /// <summary>
    /// The order the plan carries, in the <em>dataset's</em> column names. A
    /// sort on the facade's synthetic <c>OBJECTID</c> is a sort on the layer's
    /// own identity column — a plan names real columns, never the facade's
    /// field name (ADR-0037, ADR-0097) — and a request that sorts on it on a
    /// layer with no single identity column is not pushable at all, because
    /// there is no column for the plan to name.
    /// </summary>
    private static bool TryOrder(DatasetDescription dataset, EsriFeatureQuery query, out IReadOnlyList<OrderTerm>? order)
    {
        order = null;
        if (query.OrderByFields is not { Count: > 0 })
        {
            return true;
        }

        var keys = FeatureOrdering.Compile(dataset, query)
            ?? throw GeoServicesErrors.Invalid($"'orderByFields' names no orderable field in layer '{dataset.Id}'.");
        var identity = dataset.IdColumns is [var column] ? column : null;
        var terms = new List<OrderTerm>(keys.Length);
        foreach (var term in keys)
        {
            var name = term.ObjectId ? identity : dataset.Schema[term.Index].Name;
            if (name is null)
            {
                return false;
            }

            terms.Add(new OrderTerm(name, term.Descending ? SortDirection.Descending : SortDirection.Ascending));
        }

        order = terms;
        return true;
    }

    /// <summary>
    /// The count of the plan — or of its distinct combinations, which is what
    /// <c>returnCountOnly</c> together with <c>returnDistinctValues</c> asks for
    /// (the COUNT DISTINCT shape, S3). Either way the number is the store's.
    /// </summary>
    private static async Task<IResult> CountAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        FeatureQuery plan,
        EsriFeatureQuery query,
        CancellationToken cancellationToken)
    {
        if (query.ReturnDistinctValues)
        {
            var distinct = new DistinctQuery(FeatureResponseWriter.DistinctFields(dataset, query.OutFields));
            var page = await FeatureReductionFallback
                .DistinctAsync(store, dataset.Id, plan, distinct, cancellationToken)
                .ConfigureAwait(false);
            return FeatureResponseWriter.Count(page.TotalCount ?? page.Rows.Count);
        }

        var count = await FeatureReductionFallback.CountAsync(store, dataset.Id, plan, cancellationToken).ConfigureAwait(false);
        return FeatureResponseWriter.Count(count);
    }

    /// <summary>
    /// The distinct values of the projected fields, deduplicated by the store.
    /// Paging is the adapter's, over the rows the store returned, so the served
    /// <c>resultPaginationToken</c> stays the token this surface has always
    /// issued.
    /// </summary>
    private static async Task<IResult> DistinctAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        FeatureQuery plan,
        EsriFeatureQuery query,
        CoordinateReference? layerCrs,
        CancellationToken cancellationToken)
    {
        var fields = FeatureResponseWriter.DistinctFields(dataset, query.OutFields);
        var page = await FeatureReductionFallback
            .DistinctAsync(store, dataset.Id, plan, new DistinctQuery(fields), cancellationToken)
            .ConfigureAwait(false);
        return FeatureResponseWriter.DistinctValues(
            dataset,
            [.. page.Rows.Select(row => row.ToArray())],
            FeatureResponseWriter.ResolveDistinctFields(dataset, query.OutFields),
            query,
            layerCrs);
    }

    /// <summary>
    /// The feature page: the store ordered, projected and capped it, so the
    /// adapter's sort and page are not applied twice — only <c>outSR</c> and
    /// <c>geometryPrecision</c>, which are the response's business. The page's
    /// continuation is the same token this surface has always issued, derived
    /// from the start and the rows returned.
    /// </summary>
    private static async Task<IResult> FeaturesAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        FeatureQuery plan,
        EsriFeatureQuery query,
        EsriObjectIdScheme scheme,
        CoordinateReference? layerCrs,
        QueryServices services,
        CancellationToken cancellationToken)
    {
        var page = await store.QueryAsync(dataset.Id, plan, cancellationToken).ConfigureAwait(false);
        var matches = new List<MatchedFeature>(page.Features.Count());
        foreach (var feature in page.Features)
        {
            // The object id comes from the feature's own identity, not from a
            // schema index: a projected page does not carry the identity column
            // where the layer's description says it is (ADR-0037, ADR-0098).
            matches.Add(new MatchedFeature(scheme.ResolveAssigned(feature.Id), feature));
        }

        if (query.ReturnIdsOnly)
        {
            return FeatureResponseWriter.IdsOnly(matches);
        }

        if (query.ReturnUniqueIdsOnly)
        {
            return FeatureResponseWriter.UniqueIdsOnly(dataset, matches);
        }

        var features = matches
            .Select(match => FeatureProjection.TransformFeature(match, query, layerCrs, services.Transforms, services.Operations, cancellationToken))
            .ToArray();
        var offset = Math.Min(FeaturePaging.ResolveOffset(query), int.MaxValue);
        var next = offset + features.Length;
        return FeatureResponseWriter.WriteFeatures(
            dataset, layerCrs, query, features, services, page.NextCursor is not null, page.NextCursor is not null ? ResultPagination.Encode(next) : null);
    }
}
