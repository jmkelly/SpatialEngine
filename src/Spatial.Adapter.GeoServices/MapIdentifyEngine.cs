using System.Globalization;
using System.Text.Json;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer <c>identify</c> operation (spec §4.0.5): given a point or
/// envelope, a pixel tolerance and a layer selection, it returns the features
/// that intersect the geometry across the selected layers. The identify
/// geometry is buffered by the tolerance (map units derived from
/// <c>mapExtent</c>/<c>imageDisplay</c>) and matched with the engine's
/// <see cref="IGeometryOperations"/> verbs; attributes and geometry are
/// written as Esri JSON. The <c>time</c>/<c>timeRelation</c>/
/// <c>layerTimeOptions</c> temporal surface (T-059) reuses the export
/// grammar (<see cref="EsriFeatureQuery.ParseTime"/>, <see
/// cref="MapExportTime"/>) and the query-path temporal rule, so dated
/// hits filter exactly as <c>query</c> and <c>export</c> do.
/// </summary>
internal static class MapIdentifyEngine
{
    public static async Task<IResult> IdentifyAsync(
        IFeatureStore store,
        IReadOnlyList<MapLayerInfo> layers,
        EsriRequestParameters parameters,
        int mapSrid,
        IGeometryOperations operations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var identifyCrs = EsriValueParser.ParseSpatialReference(parameters.Get("sr"))
            ?? EsriLayerModel.LayerCoordinateReference(mapSrid);
        var geometry = EsriValueParser.ParseGeometry(parameters.Require("geometry"), identifyCrs);
        var tolerance = ToleranceUnits(parameters);
        var queryGeometry = tolerance > 0 ? operations.Buffer(geometry, tolerance, 8, cancellationToken) : geometry;
        var returnGeometry = parameters.GetBool("returnGeometry", true);
        var layerDefs = MapRenderEngine.ParseLayerDefs(parameters.Get("layerDefs"));
        var selected = MapLayerSelection.Select(layers, parameters.Get("layers"));
        var times = MapExportTime.ResolveTimes(
            [.. selected.Select(layer => layer.Layer)],
            EsriFeatureQuery.ParseTime(parameters.Get("time")),
            MapExportTime.ParseLayerTimeOptions(parameters.Get("layerTimeOptions")));
        _ = MapExportTime.ParseTimeRelation(parameters.Get("timeRelation"));
        var hits = await MatchAsync(new IdentifyServices(store, operations, transforms), selected, new IdentifyQuery(layerDefs, times, queryGeometry, identifyCrs, returnGeometry), cancellationToken);
        return EsriJson.Write(writer => WriteResults(writer, hits, returnGeometry));
    }

    /// <summary>The engine verbs one identify request needs, grouped so the match pipeline stays within the parameter budget.</summary>
    internal sealed record IdentifyServices(IFeatureStore Store, IGeometryOperations Operations, ICoordinateTransforms Transforms);

    /// <summary>The per-request identify selection: layer filters, the buffered query geometry and its result shape.</summary>
    internal sealed record IdentifyQuery(
        IReadOnlyDictionary<int, string>? LayerDefs,
        IReadOnlyDictionary<int, MapTimeExtent>? Times,
        IGeometry QueryGeometry,
        CoordinateReference? IdentifyCrs,
        bool ReturnGeometry);

    internal static async Task<List<IdentifyHit>> MatchAsync(
        IdentifyServices services,
        IReadOnlyList<MapLayerInfo> layers,
        IdentifyQuery query,
        CancellationToken cancellationToken)
    {
        var hits = new List<IdentifyHit>();
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CollectLayerHitsAsync(services, layer, query, hits, cancellationToken);
        }

        return hits;
    }

    /// <summary>Scans one layer's features, keeping the hits that pass the layer's filters and intersect the query.</summary>
    private static async Task CollectLayerHitsAsync(
        IdentifyServices services,
        MapLayerInfo layer,
        IdentifyQuery query,
        List<IdentifyHit> hits,
        CancellationToken cancellationToken)
    {
        var definition = query.LayerDefs?.GetValueOrDefault(layer.Layer.Id) is { } where
            ? ParseLayerDef(layer.Layer.Id, where)
            : null;
        var scheme = definition is null ? null : EsriObjectIdScheme.For(layer.Dataset);
        var layerCrs = EsriLayerModel.LayerCoordinateReference(layer.Dataset.Srid);
        var localQuery = Transform(query.QueryGeometry, layerCrs, services.Transforms, cancellationToken);
        var extent = query.Times?.GetValueOrDefault(layer.Layer.Id);
        var batches = await services.Store.ScanAsync(layer.Layer.Dataset, cancellationToken);
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal++;
            if (!MatchesFilters(scheme, layer.Dataset, definition, feature, ordinal, extent))
            {
                continue;
            }

            if (FeatureGeometry.Find(feature) is not { } geometry
                || services.Operations.Intersection(geometry, localQuery, cancellationToken).IsEmpty)
            {
                continue;
            }

            var projected = query.ReturnGeometry ? Transform(geometry, query.IdentifyCrs, services.Transforms, cancellationToken) : null;
            hits.Add(new IdentifyHit(layer, feature, projected));
        }
    }

    /// <summary>
    /// Applies one layer's attribute and temporal filters to a feature:
    /// the <c>layerDefs</c> definition expression (with the synthetic
    /// <c>OBJECTID</c> resolved as the query path does) and the layer's
    /// time extent. Geometry intersection stays with the caller.
    /// </summary>
    internal static bool MatchesFilters(
        EsriObjectIdScheme? scheme,
        DatasetDescription dataset,
        EsriFilterClause? definition,
        Feature feature,
        long ordinal,
        MapTimeExtent? extent)
    {
        if (definition is not null && !MatchesDefinition(scheme!, dataset, definition, feature, ordinal))
        {
            return false;
        }

        if (extent is not null
            && !FeatureSpatialMatcher.MatchesTime(feature, new EsriTimeExtent(extent.StartMs, extent.EndMs)))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses one layer's <c>layerDefs</c> clause. The clause was validated
    /// when the <c>layerDefs</c> object parsed, so its re-rendered form always
    /// parses; a failure here is still typed rather than silent.
    /// </summary>
    internal static EsriFilterClause ParseLayerDef(int layerId, string where) =>
        EsriFilterClause.TryParse(where, out var clause, out var error) && clause is not null
            ? clause
            : throw GeoServicesErrors.Invalid($"'layerDefs' clause for layer {layerId} is not supported: {error}.");

    /// <summary>
    /// Applies one layer's definition to a feature, resolving the synthetic
    /// <c>OBJECTID</c> exactly as the query path does.
    /// </summary>
    internal static bool MatchesDefinition(
        EsriObjectIdScheme scheme, DatasetDescription dataset, EsriFilterClause definition, Feature feature, long ordinal)
    {
        if (!scheme.TryResolve(feature, ordinal, out var objectId))
        {
            throw GeoServicesErrors.ServerError(
                $"The identity column of layer '{dataset.Id}' is not an integer.");
        }

        return definition.Matches(feature, new EsriSyntheticField(EsriLayerModel.ObjectIdField, AttributeValue.FromInt64(objectId)));
    }

    private static void WriteResults(Utf8JsonWriter writer, IReadOnlyList<IdentifyHit> hits, bool returnGeometry)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("results");
        writer.WriteStartArray();
        foreach (var hit in hits)
        {
            var displayField = MapFeatures.DisplayField(hit.Layer.Dataset.Schema);
            writer.WriteStartObject();
            writer.WriteNumber("layerId", hit.Layer.Layer.Id);
            writer.WriteString("layerName", hit.Layer.Layer.Name);
            writer.WriteString("displayFieldName", displayField);
            writer.WriteString("value", displayField.Length > 0 ? MapFeatures.StringValue(hit.Feature, displayField) : null);
            writer.WritePropertyName("attributes");
            writer.WriteStartObject();
            MapFeatures.WriteAttributes(writer, hit.Feature);
            writer.WriteEndObject();
            writer.WriteString("geometryType", EsriLayerModel.GeometryType(hit.Layer.Dataset.GeometryType));
            if (returnGeometry && hit.Geometry is { } geometry)
            {
                writer.WritePropertyName("geometry");
                EsriGeometryCodec.Write(writer, geometry);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static IGeometry Transform(IGeometry geometry, CoordinateReference? target, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
        target is { } to && geometry.CoordinateReference is { } from && from != to
            ? transforms.Transform(geometry, from.ToString(), to.ToString(), cancellationToken)
            : geometry;

    /// <summary>
    /// The identify tolerance in map units: <c>tolerance</c> pixels scaled by
    /// the map's units-per-pixel from <c>mapExtent</c>/<c>imageDisplay</c>.
    /// Zero when the client supplies neither (an exact intersection test).
    /// </summary>
    private static double ToleranceUnits(EsriRequestParameters parameters)
    {
        var pixels = ParseDouble(parameters.Get("tolerance"), 3.0);
        var mapExtent = parameters.Get("mapExtent");
        var imageDisplay = parameters.Get("imageDisplay");
        if (mapExtent is null || imageDisplay is null)
        {
            return 0;
        }

        var extent = EsriValueParser.ParseDoubles(mapExtent, "mapExtent");
        var display = EsriValueParser.ParseDoubles(imageDisplay, "imageDisplay");
        return TryUnitsPerPixel(extent, display, out var unitsPerPixel)
            ? Math.Abs(pixels) * unitsPerPixel
            : 0;
    }

    private static bool TryUnitsPerPixel(IReadOnlyList<double> extent, IReadOnlyList<double> display, out double unitsPerPixel)
    {
        unitsPerPixel = 0;
        if (extent.Count < 4 || display.Count < 2 || display[0] <= 0)
        {
            return false;
        }

        unitsPerPixel = (extent[2] - extent[0]) / display[0];
        return true;
    }

    private static double ParseDouble(string? value, double fallback) =>
        string.IsNullOrWhiteSpace(value) || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? fallback
            : parsed;

    internal sealed record IdentifyHit(MapLayerInfo Layer, Feature Feature, IGeometry? Geometry);
}
