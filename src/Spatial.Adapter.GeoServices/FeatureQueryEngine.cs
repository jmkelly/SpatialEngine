using System.Globalization;
using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

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
        var matches = await MatchAsync(dataset, store, query, queryGeometry, operations, scheme, cancellationToken);
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
            return IdsOnly(ordered);
        }

        if (query.ReturnCountOnly)
        {
            return EsriJson.Value(new EsriCountResponse(ordered.Count));
        }

        if (query.ReturnExtentOnly)
        {
            return ExtentOnly(ordered, layerCrs, query.OutSr, transforms, cancellationToken);
        }

        if (query.ReturnDistinctValues)
        {
            return DistinctValues(dataset, ordered, query, layerCrs);
        }

        var page = Page(ordered, query);
        var features = page.Items
            .Select(item => TransformFeature(item, query, layerCrs, transforms, cancellationToken))
            .ToArray();
        return WriteFeatures(dataset, layerCrs, query, features, page.Exceeded);
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
                throw new EsriInteropException(
                    EsriErrorCodes.ServerError,
                    $"The identity column of layer '{dataset.Id}' is not an integer.");
            }

            if (candidate != objectId)
            {
                continue;
            }

            var transformed = TransformFeature(new MatchedFeature(objectId, feature), query, layerCrs, transforms, cancellationToken);
            return WriteFeature(transformed, query);
        }

        throw new EsriInteropException(EsriErrorCodes.NotFound, $"Feature {objectId} does not exist in layer '{dataset.Id}'.");
    }

    private static async Task<List<MatchedFeature>> MatchAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriFeatureQuery query,
        IGeometry? queryGeometry,
        IGeometryOperations operations,
        EsriObjectIdScheme scheme,
        CancellationToken cancellationToken)
    {
        var batches = await store.ScanAsync(dataset.Id, cancellationToken);
        var matches = new List<MatchedFeature>();
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            ordinal++;
            if (!scheme.TryResolve(feature, ordinal, out var objectId))
            {
                throw new EsriInteropException(
                    EsriErrorCodes.ServerError,
                    $"The identity column of layer '{dataset.Id}' is not an integer.");
            }

            if (Matches(query, feature, objectId, queryGeometry, operations, cancellationToken))
            {
                matches.Add(new MatchedFeature(objectId, feature));
            }
        }

        return matches;
    }

    internal static bool Matches(
        EsriFeatureQuery query,
        Feature feature,
        long objectId,
        IGeometry? queryGeometry,
        IGeometryOperations operations,
        CancellationToken cancellationToken)
    {
        if (query.ObjectIds is { } ids && !ids.Contains(objectId))
        {
            return false;
        }

        if (query.Where is { } where && !where.Matches(feature))
        {
            return false;
        }

        return queryGeometry is null || SpatialMatch(feature, queryGeometry, query.SpatialRel, operations, cancellationToken);
    }

    private static bool SpatialMatch(Feature feature, IGeometry queryGeometry, string spatialRel, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        var geometry = FeatureGeometry.Find(feature);
        if (geometry is null || geometry.Envelope is not { } featureEnvelope || queryGeometry.Envelope is not { } queryEnvelope)
        {
            return false;
        }

        if (string.Equals(spatialRel, EsriFeatureQuery.EnvelopeIntersects, StringComparison.Ordinal))
        {
            return featureEnvelope.Intersects(queryEnvelope);
        }

        return !operations.Intersection(geometry, queryGeometry, cancellationToken).IsEmpty;
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
    /// each entry to a schema index plus direction. Unknown fields and geometry
    /// fields are typed invalid-argument failures (HTTP 400).
    /// </summary>
    private static OrderKey[]? CompileOrderBy(DatasetDescription dataset, EsriFeatureQuery query)
    {
        if (query.OrderByFields is not { Count: > 0 } fields)
        {
            return null;
        }

        var keys = new OrderKey[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var index = dataset.Schema.IndexOf(field.Name);
            if (index < 0)
            {
                throw EsriInteropException.Invalid(
                    $"'orderByFields' names unknown field '{field.Name}' in layer '{dataset.Id}'.");
            }

            if (dataset.Schema[index].Kind == AttributeKind.Geometry)
            {
                throw EsriInteropException.Invalid(
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
    private static List<MatchedFeature> ApplyOrderBy(List<MatchedFeature> matches, OrderKey[]? keys)
    {
        if (keys is null)
        {
            return matches;
        }

        IOrderedEnumerable<MatchedFeature>? ordered = null;
        foreach (var key in keys)
        {
            Func<MatchedFeature, AttributeValue> selector = match => match.Feature[key.Index];
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

    private static PageResult Page(List<MatchedFeature> matches, EsriFeatureQuery query)
    {
        var offset = Math.Min(query.ResultOffset ?? 0, matches.Count);
        var count = query.ResultRecordCount ?? EsriLayerModel.MaxRecordCount;
        var items = matches.Skip(offset).Take(count).ToArray();
        return new PageResult(items, offset + items.Length < matches.Count);
    }

    private static MatchedFeature TransformFeature(
        MatchedFeature match,
        EsriFeatureQuery query,
        CoordinateReference? layerCrs,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var feature = match.Feature;
        if (query.OutSr is not { } target || layerCrs is null || target == layerCrs)
        {
            return match;
        }

        var geometryIndex = FeatureGeometry.Index(feature.Schema);
        if (geometryIndex < 0 || feature[geometryIndex].Kind != AttributeKind.Geometry)
        {
            return match;
        }

        var transformed = transforms.Transform(feature[geometryIndex].GeometryValue, layerCrs.Value.ToString(), target.ToString(), cancellationToken);
        var attributes = feature.Attributes.ToArray();
        attributes[geometryIndex] = AttributeValue.FromGeometry(transformed);
        return new MatchedFeature(match.ObjectId, new Feature(feature.Id, feature.Schema, attributes));
    }

    private static IResult WriteFeature(MatchedFeature feature, EsriFeatureQuery query)
    {
        var options = new EsriFeatureWriteOptions(EsriLayerModel.ObjectIdField, feature.ObjectId, query.OutFields, query.ReturnGeometry);
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("feature");
            EsriFeatureCodec.Write(writer, feature.Feature, options);
            writer.WriteEndObject();
        });
    }

    private static IResult IdsOnly(List<MatchedFeature> matches) =>
        EsriJson.Value(new EsriObjectIdsResponse(EsriLayerModel.ObjectIdField, matches.Select(match => match.ObjectId).ToArray()));

    /// <summary>
    /// The <c>returnExtentOnly</c> response: the envelope of the full matched
    /// set (before paging), in <c>outSR</c> when supplied, else the layer SR.
    /// A matchless query yields <c>"extent": null</c>.
    /// </summary>
    private static IResult ExtentOnly(
        IReadOnlyList<MatchedFeature> matches,
        CoordinateReference? layerCrs,
        CoordinateReference? outSr,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var extent = Envelope.Empty;
        foreach (var match in matches)
        {
            if (FeatureGeometry.Find(match.Feature) is not { } geometry)
            {
                continue;
            }

            var projected = TransformGeometry(geometry, layerCrs, outSr, transforms, cancellationToken);
            if (projected.Envelope is { } envelope)
            {
                extent = extent.Union(envelope);
            }
        }

        return WriteExtent(extent, outSr ?? layerCrs);
    }

    private static IResult WriteExtent(Envelope extent, CoordinateReference? coordinateReference) =>
        EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            if (extent.IsEmpty)
            {
                writer.WriteNull("extent");
            }
            else
            {
                writer.WritePropertyName("extent");
                writer.WriteStartObject();
                writer.WriteNumber("xmin", extent.MinX);
                writer.WriteNumber("ymin", extent.MinY);
                writer.WriteNumber("xmax", extent.MaxX);
                writer.WriteNumber("ymax", extent.MaxY);
                WriteSpatialReference(writer, coordinateReference);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        });

    /// <summary>
    /// The <c>returnDistinctValues</c> response: the deduplicated combinations
    /// of the projected fields, no geometry. Paging is applied after dedupe.
    /// </summary>
    private static IResult DistinctValues(
        DatasetDescription dataset,
        IReadOnlyList<MatchedFeature> matches,
        EsriFeatureQuery query,
        CoordinateReference? layerCrs)
    {
        var fields = ResolveDistinctFields(dataset, query.OutFields);
        var rows = new List<AttributeValue[]>();
        var seen = new HashSet<AttributeValue[]>(AttributeRowComparer.Instance);
        foreach (var match in matches)
        {
            var row = new AttributeValue[fields.Count];
            for (var i = 0; i < fields.Count; i++)
            {
                row[i] = match.Feature[fields[i].Index];
            }

            if (seen.Add(row))
            {
                rows.Add(row);
            }
        }

        var offset = Math.Min(query.ResultOffset ?? 0, rows.Count);
        var count = query.ResultRecordCount ?? EsriLayerModel.MaxRecordCount;
        var page = rows.Skip(offset).Take(count).ToArray();
        return WriteDistinctValues(dataset, query.OutSr ?? layerCrs, fields, page, offset + page.Length < rows.Count);
    }

    private static List<DistinctField> ResolveDistinctFields(DatasetDescription dataset, IReadOnlyList<string>? outFields)
    {
        var schema = dataset.Schema;
        var names = outFields is { Count: > 0 }
            ? outFields
            : schema.Fields.Where(field => field.Kind != AttributeKind.Geometry).Select(field => field.Name).ToArray();
        var fields = new List<DistinctField>(names.Count);
        foreach (var name in names)
        {
            var index = schema.IndexOf(name);
            if (index < 0 || schema[index].Kind == AttributeKind.Geometry)
            {
                throw EsriInteropException.Invalid(
                    $"The 'outFields' value '{name}' is not a distinctable attribute of layer '{dataset.Id}'.");
            }

            fields.Add(new DistinctField(name, index));
        }

        return fields;
    }

    private static IResult WriteDistinctValues(
        DatasetDescription dataset,
        CoordinateReference? coordinateReference,
        IReadOnlyList<DistinctField> fields,
        IReadOnlyList<AttributeValue[]> rows,
        bool exceeded)
    {
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("objectIdFieldName", EsriLayerModel.ObjectIdField);
            writer.WriteString("geometryType", EsriLayerModel.GeometryType(dataset.GeometryType));
            WriteSpatialReference(writer, coordinateReference);
            WriteFields(writer, dataset);
            writer.WritePropertyName("features");
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("attributes");
                writer.WriteStartObject();
                for (var i = 0; i < fields.Count; i++)
                {
                    EsriAttributeCodec.Write(writer, fields[i].Name, row[i]);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("exceededTransferLimit", exceeded);
            writer.WriteEndObject();
        });
    }

    private static IGeometry TransformGeometry(
        IGeometry geometry,
        CoordinateReference? source,
        CoordinateReference? target,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        if (target is not { } to || source is not { } from || from == to)
        {
            return geometry;
        }

        return transforms.Transform(geometry, from.ToString(), to.ToString(), cancellationToken);
    }

    private static IResult WriteFeatures(
        DatasetDescription dataset,
        CoordinateReference? layerCrs,
        EsriFeatureQuery query,
        IReadOnlyList<MatchedFeature> features,
        bool exceeded)
    {
        var options = new EsriFeatureWriteOptions(EsriLayerModel.ObjectIdField, 0, query.OutFields, query.ReturnGeometry);
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("objectIdFieldName", EsriLayerModel.ObjectIdField);
            writer.WriteString("geometryType", EsriLayerModel.GeometryType(dataset.GeometryType));
            WriteSpatialReference(writer, query.OutSr ?? layerCrs);
            WriteFields(writer, dataset);
            writer.WritePropertyName("features");
            writer.WriteStartArray();
            foreach (var feature in features)
            {
                EsriFeatureCodec.Write(writer, feature.Feature, options with { ObjectId = feature.ObjectId });
            }

            writer.WriteEndArray();
            writer.WriteBoolean("exceededTransferLimit", exceeded);
            writer.WriteEndObject();
        });
    }

    private static void WriteSpatialReference(Utf8JsonWriter writer, CoordinateReference? coordinateReference)
    {
        if (coordinateReference is { } crs && IsMapped(crs))
        {
            EsriSpatialReference.Write(writer, crs);
            return;
        }

        writer.WriteNull("spatialReference");
    }

    private static bool IsMapped(CoordinateReference crs) =>
        string.Equals(crs.Authority, "EPSG", StringComparison.OrdinalIgnoreCase)
        && int.TryParse(crs.Code, NumberStyles.None, CultureInfo.InvariantCulture, out var epsg)
        && WkidMap.TryFromEpsg(epsg, out _);

    private static void WriteFields(Utf8JsonWriter writer, DatasetDescription dataset)
    {
        writer.WritePropertyName("fields");
        writer.WriteStartArray();
        WriteField(writer, EsriLayerModel.ObjectIdField, EsriFieldType.Oid, false, false);
        foreach (var field in dataset.Schema.Fields)
        {
            // A raster catalog's schema already carries the identity column;
            // the synthetic OBJECTID above is authoritative, so do not repeat it.
            if (field.Name == EsriLayerModel.ObjectIdField)
            {
                continue;
            }

            WriteField(writer, field.Name, EsriFieldType.FromAttributeKind(field.Kind), field.Nullable, field.Kind != AttributeKind.Geometry);
        }

        writer.WriteEndArray();
    }

    private static void WriteField(Utf8JsonWriter writer, string name, string type, bool nullable, bool editable)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("type", type);
        writer.WriteString("alias", name);
        writer.WriteBoolean("nullable", nullable);
        writer.WriteBoolean("editable", editable);
        writer.WriteEndObject();
    }

    internal sealed record MatchedFeature(long ObjectId, Feature Feature);

    /// <summary>One compiled <c>orderByFields</c> key: a schema index and direction.</summary>
    private sealed record OrderKey(int Index, bool Descending);

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

    private sealed record PageResult(IReadOnlyList<MatchedFeature> Items, bool Exceeded);

    /// <summary>One projected field of a distinct-values request.</summary>
    private readonly record struct DistinctField(string Name, int Index);

    /// <summary>Structural equality for projected distinct-value rows.</summary>
    private sealed class AttributeRowComparer : IEqualityComparer<AttributeValue[]>
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
