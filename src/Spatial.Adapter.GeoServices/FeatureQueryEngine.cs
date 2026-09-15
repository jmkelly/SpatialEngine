using System.Globalization;
using System.Text.Json;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Evaluates Feature Service queries (spec §9.1.4) and the Feature (object)
/// resource (spec §9.1.2): matches the supported query subset against the
/// store, applies <c>orderByFields</c> and paging, reprojects to <c>outSR</c>,
/// and writes the Esri JSON responses. Split out of <see cref="FeatureService"/>
/// so the protocol facade keeps only its per-operation surface and the query
/// path's fan-out stays cohesive (ADR-0040).
/// </summary>
internal static class FeatureQueryEngine
{
    /// <summary>Executes a query and writes the spec §9.1.4.3 response.</summary>
    public static async Task<IResult> QueryAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriFeatureQuery query,
        IGeometryOperations operations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var layerCrs = EsriLayerModel.LayerCoordinateReference(dataset.Srid);
        var scheme = EsriObjectIdScheme.For(dataset);
        var queryGeometry = TransformQueryGeometry(query.Geometry, layerCrs, transforms, cancellationToken);
        var matches = await FeatureSpatialMatcher.MatchAsync(new FeatureSpatialMatcher.QuerySpec(dataset, store, query, queryGeometry, operations, scheme), cancellationToken);
        return Project(dataset, matches, query, layerCrs, transforms, cancellationToken);
    }

    /// <summary>
    /// Shapes a matched feature set into the spec §9.1.4.3 response: ordering,
    /// the mutually exclusive result shapes, paging, <c>outSR</c> reprojection
    /// and the Esri JSON. Shared by the Feature/Map query path and the Image
    /// Service catalog query (spec §8.0.5, ADR-0051), whose catalog is a
    /// feature-like table of raster items.
    /// </summary>
    internal static IResult Project(
        DatasetDescription dataset,
        IReadOnlyList<MatchedFeature> matches,
        EsriFeatureQuery query,
        CoordinateReference? layerCrs,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var ordered = ApplyOrderBy([.. matches], CompileOrderBy(dataset, query));
        if (query.ReturnIdsOnly)
        {
            return FeatureResponseWriter.IdsOnly(ordered);
        }

        if (query.ReturnCountOnly)
        {
            return FeatureResponseWriter.CountResponse(dataset, ordered, query);
        }

        if (query.ReturnExtentOnly)
        {
            return FeatureResponseWriter.ExtentOnly(ordered, layerCrs, query.OutSr, transforms, cancellationToken);
        }

        if (query.ReturnDistinctValues)
        {
            return FeatureResponseWriter.DistinctValues(dataset, ordered, query, layerCrs);
        }

        if (query.OutStatistics is not null)
        {
            return FeatureStatisticsEngine.Statistics(dataset, ordered, query);
        }

        if (query.ReturnUniqueIdsOnly)
        {
            return FeatureResponseWriter.UniqueIdsOnly(dataset, ordered);
        }

        var page = Page(ordered, query);
        var features = page.Items
            .Select(item => FeatureProjection.TransformFeature(item, query, layerCrs, transforms, cancellationToken))
            .ToArray();
        return FeatureResponseWriter.WriteFeatures(dataset, layerCrs, query, features, page.Exceeded, page.NextToken);
    }



    /// <summary>
    /// Reads one feature by its Esri <c>OBJECTID</c> (the Feature resource,
    /// spec §9.1.2) and writes the <c>{"feature": ...}</c> envelope. The
    /// object id is resolved exactly as <c>query</c> does — the identity
    /// column when the layer has one, otherwise the scan ordinal — so the
    /// resource agrees with <c>returnIdsOnly</c>.
    /// </summary>
    public static async Task<IResult> FeatureAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        long objectId,
        EsriFeatureQuery query,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var layerCrs = EsriLayerModel.LayerCoordinateReference(dataset.Srid);
        var scheme = EsriObjectIdScheme.For(dataset);
        var batches = await store.ScanAsync(dataset.Id, cancellationToken);
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            ordinal++;
            if (!scheme.TryResolve(feature, ordinal, out var candidate))
            {
                throw GeoServicesErrors.ServerError(
                    $"The identity column of layer '{dataset.Id}' is not an integer.");
            }

            if (candidate != objectId)
            {
                continue;
            }

            var transformed = FeatureProjection.TransformFeature(new MatchedFeature(objectId, feature), query, layerCrs, transforms, cancellationToken);
            return FeatureResponseWriter.WriteFeature(transformed, query);
        }

        throw GeoServicesErrors.NotFound($"Feature {objectId} does not exist in layer '{dataset.Id}'.");
    }


    internal static IGeometry? TransformQueryGeometry(
        IGeometry? geometry,
        CoordinateReference? layerCrs,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        if (geometry is null || layerCrs is null || geometry.CoordinateReference is not { } source || source == layerCrs)
        {
            return geometry;
        }

        return transforms.Transform(geometry, source.ToString(), layerCrs.Value.ToString(), cancellationToken);
    }

    /// <summary>
    /// Validates <c>orderByFields</c> against the dataset schema and compiles
    /// each entry to a key plus direction. <c>OBJECTID</c> is accepted even
    /// though it is synthetic, because it is the layer's advertised object-id
    /// field and clients (QGIS) order by it for a stable paged sequence.
    /// Unknown fields and geometry fields are typed invalid-argument failures
    /// (HTTP 400).
    /// </summary>
    internal static OrderKey[]? CompileOrderBy(DatasetDescription dataset, EsriFeatureQuery query)
    {
        if (query.OrderByFields is not { Count: > 0 } fields)
        {
            return null;
        }

        var keys = new OrderKey[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (string.Equals(field.Name, EsriLayerModel.ObjectIdField, StringComparison.OrdinalIgnoreCase))
            {
                keys[i] = new OrderKey(-1, field.Descending, ObjectId: true);
                continue;
            }

            var index = dataset.Schema.IndexOf(field.Name);
            if (index < 0)
            {
                throw GeoServicesErrors.Invalid(
                    $"'orderByFields' names unknown field '{field.Name}' in layer '{dataset.Id}'.");
            }

            if (dataset.Schema[index].Kind == AttributeKind.Geometry)
            {
                throw GeoServicesErrors.Invalid(
                    $"'orderByFields' cannot order by geometry field '{field.Name}' in layer '{dataset.Id}'.");
            }

            keys[i] = new OrderKey(index, field.Descending);
        }

        return keys;
    }

    /// <summary>
    /// Applies the compiled ordering to the matched features before paging.
    /// LINQ's ordering is a stable sort, so equal keys keep their scan order.
    /// </summary>
    internal static List<MatchedFeature> ApplyOrderBy(List<MatchedFeature> matches, OrderKey[]? keys)
    {
        if (keys is null)
        {
            return matches;
        }

        IOrderedEnumerable<MatchedFeature>? ordered = null;
        foreach (var key in keys)
        {
            Func<MatchedFeature, AttributeValue> selector = key.ObjectId
                ? match => AttributeValue.FromInt64(match.ObjectId)
                : match => match.Feature[key.Index];
            ordered = ordered is null
                ? Order(matches, selector, key.Descending)
                : ThenOrder(ordered, selector, key.Descending);
        }

        return ordered!.ToList();
    }

    private static IOrderedEnumerable<MatchedFeature> Order(
        IEnumerable<MatchedFeature> matches, Func<MatchedFeature, AttributeValue> selector, bool descending) =>
        descending
            ? matches.OrderByDescending(selector, AttributeValueComparer.Instance)
            : matches.OrderBy(selector, AttributeValueComparer.Instance);

    private static IOrderedEnumerable<MatchedFeature> ThenOrder(
        IOrderedEnumerable<MatchedFeature> ordered, Func<MatchedFeature, AttributeValue> selector, bool descending) =>
        descending
            ? ordered.ThenByDescending(selector, AttributeValueComparer.Instance)
            : ordered.ThenBy(selector, AttributeValueComparer.Instance);

    internal static PageResult Page(List<MatchedFeature> matches, EsriFeatureQuery query)
    {
        var offset = Math.Min(ResolveOffset(query), matches.Count);
        var count = EffectivePageSize(query);
        var items = matches.Skip(offset).Take(count).ToArray();
        var exceeded = offset + items.Length < matches.Count;
        return new PageResult(items, exceeded, exceeded ? ResultPagination.Encode(offset + items.Length) : null);
    }

    /// <summary>
    /// The page start: the opaque <c>resultPaginationToken</c> cursor when
    /// the client continues a token workflow, else <c>resultOffset</c>. Parse
    /// already rejects the combination, so the token simply wins by presence.
    /// </summary>
    internal static int ResolveOffset(EsriFeatureQuery query) =>
        query.ResultPaginationToken is { } token ? ResultPagination.Decode(token) : query.ResultOffset ?? 0;


    /// <summary>
    /// The effective page cap: <c>maxRecordCount × maxRecordCountFactor</c>
    /// (T-021). <c>returnExceededLimitFeatures</c> is accepted so the REST JS
    /// <c>queryAllFeatures</c> loop runs unmodified; the
    /// <c>exceededTransferLimit</c> flag stays correct either way.
    /// </summary>
    internal static int EffectivePageSize(EsriFeatureQuery query)
    {
        var cap = EsriLayerModel.MaxRecordCount * (query.MaxRecordCountFactor ?? 1);
        return Math.Min(query.ResultRecordCount ?? cap, cap);
    }


























    internal sealed record MatchedFeature(long ObjectId, Feature Feature);

    /// <summary>One compiled <c>orderByFields</c> key: a schema index and direction, or the synthetic object id.</summary>
    internal sealed record OrderKey(int Index, bool Descending, bool ObjectId = false);

    /// <summary>
    /// Orders attribute values of the kinds a schema can declare. Nulls sort
    /// last in ascending order (and therefore first when the key is reversed
    /// for DESC), matching the conventional SQL default. Non-null values of a
    /// key always share one kind because the dataset schema fixes the column
    /// kind; a defensive fallback compares the kinds when they do not.
    /// </summary>
    internal sealed class AttributeValueComparer : IComparer<AttributeValue>
    {
        public static readonly AttributeValueComparer Instance = new();

        public int Compare(AttributeValue left, AttributeValue right)
        {
            if (left.IsNull || right.IsNull)
            {
                return CompareNulls(left, right);
            }

            return CompareValues(left, right);
        }

        private static int CompareNulls(AttributeValue left, AttributeValue right) =>
            left.IsNull ? (right.IsNull ? 0 : 1) : -1;

        private static int CompareValues(AttributeValue left, AttributeValue right)
        {
            if (left.Kind != right.Kind)
            {
                return left.Kind.CompareTo(right.Kind);
            }

            return CompareSameKind(left, right);
        }

        private static int CompareSameKind(AttributeValue left, AttributeValue right) =>
            KindComparers.TryGetValue(left.Kind, out var compare) ? compare(left, right) : 0;

        private static readonly Dictionary<AttributeKind, Func<AttributeValue, AttributeValue, int>> KindComparers = new()
        {
            [AttributeKind.Boolean] = (left, right) => left.BooleanValue.CompareTo(right.BooleanValue),
            [AttributeKind.Int64] = (left, right) => left.Int64Value.CompareTo(right.Int64Value),
            [AttributeKind.Double] = (left, right) => left.DoubleValue.CompareTo(right.DoubleValue),
            [AttributeKind.String] = (left, right) => string.CompareOrdinal(left.StringValue, right.StringValue),
            [AttributeKind.DateTimeOffset] = (left, right) => left.DateTimeOffsetValue.UtcTicks.CompareTo(right.DateTimeOffsetValue.UtcTicks),
            [AttributeKind.Guid] = (left, right) => left.GuidValue.CompareTo(right.GuidValue),
        };
    }

    internal sealed record PageResult(IReadOnlyList<MatchedFeature> Items, bool Exceeded, string? NextToken);



    /// <summary>Structural equality for projected distinct-value rows.</summary>
    internal sealed class AttributeRowComparer : IEqualityComparer<AttributeValue[]>
    {
        public static AttributeRowComparer Instance { get; } = new();

        public bool Equals(AttributeValue[]? left, AttributeValue[]? right) =>
            ReferenceEquals(left, right)
            || (left is not null && right is not null && left.AsSpan().SequenceEqual(right));

        public int GetHashCode(AttributeValue[] row)
        {
            var hash = new HashCode();
            foreach (var value in row)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
