using System.Globalization;
using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The read-only Feature Service (spec §9): the <c>FeatureServer</c> root,
/// layer metadata and <c>query</c>. Features come from the engine's keyed
/// <see cref="IFeatureStore"/>; the facade maps them to Esri JSON, applies
/// the supported query subset, and never forwards client text as SQL. Editing
/// is out of scope until a follow-up ADR extends the store contracts
/// (ADR-0035 §5).
/// </summary>
internal static class FeatureService
{
    private const double CurrentVersion = 10.0;

    /// <summary>Builds the <c>FeatureServer</c> root (spec §9.0).</summary>
    public static EsriFeatureServerRoot Root(IReadOnlyList<DatasetSummary> datasets)
    {
        var layers = datasets.Select((dataset, index) => EsriLayerModel.Reference(index, dataset)).ToArray();
        return new EsriFeatureServerRoot(
            CurrentVersion,
            "SpatialEngine Feature Service",
            false,
            "JSON",
            "Query",
            EsriLayerModel.MaxRecordCount,
            layers,
            []);
    }

    /// <summary>Builds one layer's metadata (spec §9.1).</summary>
    public static EsriLayer Layer(int layerId, DatasetDescription dataset) =>
        EsriLayerModel.Describe(layerId, dataset);

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
        var queryGeometry = TransformQueryGeometry(query.Geometry, layerCrs, transforms, cancellationToken);
        var matches = await MatchAsync(dataset, store, query, queryGeometry, operations, cancellationToken);
        if (query.ReturnIdsOnly)
        {
            return IdsOnly(matches);
        }

        if (query.ReturnCountOnly)
        {
            return EsriJson.Value(new EsriCountResponse(matches.Count));
        }

        var page = Page(matches, query);
        var features = page.Items
            .Select(item => TransformFeature(item, query, layerCrs, transforms, cancellationToken))
            .ToArray();
        return WriteFeatures(dataset, layerCrs, query, features, page.Exceeded);
    }

    private static async Task<List<MatchedFeature>> MatchAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriFeatureQuery query,
        IGeometry? queryGeometry,
        IGeometryOperations operations,
        CancellationToken cancellationToken)
    {
        var batches = await store.ScanAsync(dataset.Id, cancellationToken);
        var matches = new List<MatchedFeature>();
        long objectId = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            objectId++;
            if (Matches(query, feature, objectId, queryGeometry, operations, cancellationToken))
            {
                matches.Add(new MatchedFeature(objectId, feature));
            }
        }

        return matches;
    }

    private static bool Matches(
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
        var geometry = FindGeometry(feature);
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

    private static IGeometry? TransformQueryGeometry(
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

        var geometryIndex = GeometryIndex(feature.Schema);
        if (geometryIndex < 0 || feature[geometryIndex].Kind != AttributeKind.Geometry)
        {
            return match;
        }

        var transformed = transforms.Transform(feature[geometryIndex].GeometryValue, layerCrs.Value.ToString(), target.ToString(), cancellationToken);
        var attributes = feature.Attributes.ToArray();
        attributes[geometryIndex] = AttributeValue.FromGeometry(transformed);
        return new MatchedFeature(match.ObjectId, new Feature(feature.Id, feature.Schema, attributes));
    }

    private static IResult IdsOnly(List<MatchedFeature> matches) =>
        EsriJson.Value(new EsriObjectIdsResponse(EsriLayerModel.ObjectIdField, matches.Select(match => match.ObjectId).ToArray()));

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
        WriteField(writer, EsriLayerModel.ObjectIdField, EsriFieldType.Oid, false);
        foreach (var field in dataset.Schema.Fields)
        {
            WriteField(writer, field.Name, EsriFieldType.FromAttributeKind(field.Kind), field.Nullable);
        }

        writer.WriteEndArray();
    }

    private static void WriteField(Utf8JsonWriter writer, string name, string type, bool nullable)
    {
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("type", type);
        writer.WriteString("alias", name);
        writer.WriteBoolean("nullable", nullable);
        writer.WriteBoolean("editable", false);
        writer.WriteEndObject();
    }

    private static IGeometry? FindGeometry(Feature feature)
    {
        var index = GeometryIndex(feature.Schema);
        return index >= 0 && feature[index].Kind == AttributeKind.Geometry ? feature[index].GeometryValue : null;
    }

    private static int GeometryIndex(FeatureSchema schema)
    {
        for (var i = 0; i < schema.Count; i++)
        {
            if (schema[i].Kind == AttributeKind.Geometry)
            {
                return i;
            }
        }

        return -1;
    }

    private sealed record MatchedFeature(long ObjectId, Feature Feature);

    private sealed record PageResult(IReadOnlyList<MatchedFeature> Items, bool Exceeded);
}

/// <summary>The <c>returnIdsOnly</c> response (spec §9.1.4.4).</summary>
internal sealed record EsriObjectIdsResponse(string ObjectIdFieldName, IReadOnlyList<long> ObjectIds);

/// <summary>The <c>returnCountOnly</c> response (10.x addition).</summary>
internal sealed record EsriCountResponse(int Count);
