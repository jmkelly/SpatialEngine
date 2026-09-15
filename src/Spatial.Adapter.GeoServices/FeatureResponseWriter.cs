using System.Globalization;
using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Esri JSON response writers: per-layer service-query assembly, the
/// feature/ids/extent/distinct shapes, field metadata and paging tokens.
/// Split out of <see cref="FeatureQueryEngine"/> so the query facade keeps
/// only orchestration and the response-shaping fan-out (codecs, field
/// metadata, layer model) lives with the code that uses it (ADR-0040).
/// </summary>
internal static class FeatureResponseWriter
{
    /// <summary>
    /// Executes a service-level query (S1 query-feature-service/) and writes
    /// the <c>{"layers": [...]}</c> response: one feature set, count, or id
    /// list per layer, in layer-id order. Each layer already carries its
    /// effective query (shared parameters plus its <c>layerDefs</c>
    /// overrides), so this method only matches, pages and projects per layer.
    /// Layer-level result shapes (extent, distinct, statistics, unique ids)
    /// are rejected by <see cref="FeatureServiceQuery.RejectLayerOnlyShapes"/>
    /// before this runs; only the full, count and ids shapes arrive here.
    /// </summary>
    internal static async Task<IResult> ServiceQueryAsync(
        IReadOnlyList<ServiceLayerQuery> layers,
        IFeatureStore store,
        EsriFeatureQuery shared,
        IGeometryOperations operations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(shared);
        FeatureServiceQuery.RejectLayerOnlyShapes(shared);
        var ordered = layers.OrderBy(layer => layer.Id).ToArray();
        var matched = new List<(ServiceLayerQuery Layer, List<FeatureQueryEngine.MatchedFeature> Matches)>(ordered.Length);
        foreach (var layer in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layerCrs = EsriLayerModel.LayerCoordinateReference(layer.Description.Srid);
            var scheme = EsriObjectIdScheme.For(layer.Description);
            var queryGeometry = FeatureQueryEngine.TransformQueryGeometry(layer.Query.Geometry, layerCrs, transforms, cancellationToken);
            matched.Add((layer, await FeatureSpatialMatcher.MatchAsync(new FeatureSpatialMatcher.QuerySpec(layer.Description, store, layer.Query, queryGeometry, operations, scheme), cancellationToken)));
        }

        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("layers");
            writer.WriteStartArray();
            foreach (var (layer, matches) in matched)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteServiceLayer(writer, layer, matches, transforms, cancellationToken);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>
    /// Writes one service-query layer entry (S1 response syntax): the full
    /// feature set, the count, or the id list. Paging applies per layer, and
    /// tables omit the layer-only <c>geometryType</c>/<c>spatialReference</c>
    /// keys, exactly as the layer query shapes they mirror.
    /// </summary>
    private static void WriteServiceLayer(
        Utf8JsonWriter writer,
        ServiceLayerQuery layer,
        List<FeatureQueryEngine.MatchedFeature> matches,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var ordered = FeatureQueryEngine.ApplyOrderBy(matches, FeatureQueryEngine.CompileOrderBy(layer.Description, layer.Query));
        writer.WriteStartObject();
        writer.WriteNumber("id", layer.Id);
        if (layer.Query.ReturnCountOnly)
        {
            writer.WriteNumber("count", ordered.Count);
            writer.WriteEndObject();
            return;
        }

        if (layer.Query.ReturnIdsOnly)
        {
            writer.WriteString("objectIdFieldName", EsriLayerModel.ObjectIdField);
            writer.WritePropertyName("objectIds");
            writer.WriteStartArray();
            foreach (var match in ordered)
            {
                writer.WriteNumberValue(match.ObjectId);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            return;
        }

        var layerCrs = EsriLayerModel.LayerCoordinateReference(layer.Description.Srid);
        var page = FeatureQueryEngine.Page(ordered, layer.Query);
        var options = new EsriFeatureWriteOptions(EsriLayerModel.ObjectIdField, 0, layer.Query.OutFields, layer.Query.ReturnGeometry, layer.Query.ReturnEnvelope);
        writer.WriteString("objectIdFieldName", EsriLayerModel.ObjectIdField);
        writer.WriteString("globalIdFieldName", string.Empty);
        if (!layer.IsTable)
        {
            writer.WriteString("geometryType", EsriLayerModel.GeometryType(layer.Description.GeometryType));
            WriteSpatialReference(writer, layer.Query.OutSr ?? layerCrs);
        }

        WriteFields(writer, layer.Description);
        writer.WritePropertyName("features");
        writer.WriteStartArray();
        foreach (var match in page.Items)
        {
            var transformed = FeatureProjection.TransformFeature(match, layer.Query, layerCrs, transforms, cancellationToken);
            EsriFeatureCodec.Write(writer, transformed.Feature, options with { ObjectId = transformed.ObjectId });
        }

        writer.WriteEndArray();
        writer.WriteBoolean("exceededTransferLimit", page.Exceeded);
        WritePaginationToken(writer, page.NextToken);
        writer.WriteEndObject();
    }

    /// <summary>
    /// The <c>returnUniqueIdsOnly</c> response (spec §9.1.4, 11.5+): the
    /// string-ID analogue of <see cref="IdsOnly"/>, symmetric in shape.
    /// Layers without a string-or-guid unique-id model never reach here — the match
    /// loops reject the param first — so a missing scheme is defensive.
    /// </summary>
    internal static IResult UniqueIdsOnly(DatasetDescription dataset, IReadOnlyList<FeatureQueryEngine.MatchedFeature> matches)
    {
        var scheme = EsriUniqueIdScheme.For(dataset)
            ?? throw GeoServicesErrors.Invalid(
                $"The 'returnUniqueIdsOnly' parameter is not supported on layer '{dataset.Id}': the layer has no string or guid unique-id field; address its integer features with 'objectIds'.");
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("uniqueIdFieldName", scheme.FieldName);
            writer.WritePropertyName("uniqueIds");
            writer.WriteStartArray();
            foreach (var match in matches)
            {
                writer.WriteStringValue(scheme.Resolve(match.Feature, dataset));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    internal static IResult WriteFeature(FeatureQueryEngine.MatchedFeature feature, EsriFeatureQuery query)
    {
        var options = new EsriFeatureWriteOptions(EsriLayerModel.ObjectIdField, feature.ObjectId, query.OutFields, query.ReturnGeometry, query.ReturnEnvelope);
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("feature");
            EsriFeatureCodec.Write(writer, feature.Feature, options);
            writer.WriteEndObject();
        });
    }

    internal static IResult IdsOnly(List<FeatureQueryEngine.MatchedFeature> matches) =>
        EsriJson.Value(new EsriObjectIdsResponse(EsriLayerModel.ObjectIdField, matches.Select(match => match.ObjectId).ToArray()));

    /// <summary>
    /// The <c>returnExtentOnly</c> response: the envelope of the full matched
    /// set (before paging), in <c>outSR</c> when supplied, else the layer SR.
    /// A matchless query yields <c>"extent": null</c>.
    /// </summary>
    internal static IResult ExtentOnly(
        IReadOnlyList<FeatureQueryEngine.MatchedFeature> matches,
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

            var projected = FeatureProjection.TransformGeometry(geometry, layerCrs, outSr, transforms, cancellationToken);
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
    /// The COUNT DISTINCT response (S3): the number of deduplicated
    /// combinations of the projected fields, the count analogue of
    /// <see cref="DistinctValues"/>. Backs the advertised
    /// <c>supportsCountDistinct</c> flag.
    /// </summary>
    /// <summary>
    /// The <c>returnCountOnly</c> response: either the COUNT DISTINCT shape
    /// or the plain matched-set count.
    /// </summary>
    internal static IResult CountResponse(
        DatasetDescription dataset,
        IReadOnlyList<FeatureQueryEngine.MatchedFeature> matches,
        EsriFeatureQuery query) =>
        query.ReturnDistinctValues
            ? DistinctCount(dataset, matches, query)
            : EsriJson.Value(new EsriCountResponse(matches.Count));

    internal static IResult DistinctCount(
        DatasetDescription dataset,
        IReadOnlyList<FeatureQueryEngine.MatchedFeature> matches,
        EsriFeatureQuery query)
    {
        var fields = ResolveDistinctFields(dataset, query.OutFields);
        return EsriJson.Value(new EsriCountResponse(DistinctRows(matches, fields).Count));
    }

    /// <summary>
    /// The <c>returnDistinctValues</c> response: the deduplicated combinations
    /// of the projected fields, no geometry. Paging is applied after dedupe.
    /// </summary>
    internal static IResult DistinctValues(
        DatasetDescription dataset,
        IReadOnlyList<FeatureQueryEngine.MatchedFeature> matches,
        EsriFeatureQuery query,
        CoordinateReference? layerCrs)
    {
        var fields = ResolveDistinctFields(dataset, query.OutFields);
        var rows = DistinctRows(matches, fields);
        var offset = Math.Min(FeatureQueryEngine.ResolveOffset(query), rows.Count);
        var count = FeatureQueryEngine.EffectivePageSize(query);
        var page = rows.Skip(offset).Take(count).ToArray();
        var exceeded = offset + page.Length < rows.Count;
        return WriteDistinctValues(dataset, query.OutSr ?? layerCrs, fields, page, exceeded, exceeded ? ResultPagination.Encode(offset + page.Length) : null);
    }

    /// <summary>
    /// Deduplicates the projected fields over the matched set, preserving
    /// first-seen order. Shared by the distinct-values response and the
    /// COUNT DISTINCT response so both agree on what "distinct" means.
    /// </summary>
    private static List<AttributeValue[]> DistinctRows(IReadOnlyList<FeatureQueryEngine.MatchedFeature> matches, IReadOnlyList<DistinctField> fields)
    {
        var rows = new List<AttributeValue[]>();
        var seen = new HashSet<AttributeValue[]>(FeatureQueryEngine.AttributeRowComparer.Instance);
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

        return rows;
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
                throw GeoServicesErrors.Invalid(
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
        bool exceeded,
        string? nextToken)
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
            WritePaginationToken(writer, nextToken);
            writer.WriteEndObject();
        });
    }

    internal static IResult WriteFeatures(
        DatasetDescription dataset,
        CoordinateReference? layerCrs,
        EsriFeatureQuery query,
        IReadOnlyList<FeatureQueryEngine.MatchedFeature> features,
        bool exceeded,
        string? nextToken)
    {
        var options = new EsriFeatureWriteOptions(EsriLayerModel.ObjectIdField, 0, query.OutFields, query.ReturnGeometry, query.ReturnEnvelope);
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
            WritePaginationToken(writer, nextToken);
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

    internal static void WriteField(Utf8JsonWriter writer, string name, string type, bool nullable, bool editable)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("type", type);
        writer.WriteString("alias", name);
        writer.WriteBoolean("nullable", nullable);
        writer.WriteBoolean("editable", editable);
        writer.WriteEndObject();
    }

    /// <summary>Writes the next-page cursor when the page filled up; the final page carries none.</summary>
    internal static void WritePaginationToken(Utf8JsonWriter writer, string? nextToken)
    {
        if (nextToken is not null)
        {
            writer.WriteString("resultPaginationToken", nextToken);
        }
    }

    /// <summary>One projected field of a distinct-values request.</summary>
    private readonly record struct DistinctField(string Name, int Index);
}
