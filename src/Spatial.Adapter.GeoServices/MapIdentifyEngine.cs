using System.Globalization;
using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

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
        var hits = await MatchAsync(store, selected, layerDefs, times, queryGeometry, identifyCrs, returnGeometry, operations, transforms, cancellationToken);
        return EsriJson.Write(writer => WriteResults(writer, hits, returnGeometry));
    }

    private static async Task<List<IdentifyHit>> MatchAsync(
        IFeatureStore store,
        IReadOnlyList<MapLayerInfo> layers,
        IReadOnlyDictionary<int, string>? layerDefs,
        IReadOnlyDictionary<int, MapTimeExtent>? times,
        IGeometry queryGeometry,
        CoordinateReference? identifyCrs,
        bool returnGeometry,
        IGeometryOperations operations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var hits = new List<IdentifyHit>();
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = layerDefs?.GetValueOrDefault(layer.Layer.Id) is { } where
                ? ParseLayerDef(layer.Layer.Id, where)
                : null;
            var scheme = definition is null ? null : EsriObjectIdScheme.For(layer.Dataset);
            var layerCrs = EsriLayerModel.LayerCoordinateReference(layer.Dataset.Srid);
            var localQuery = Transform(queryGeometry, layerCrs, transforms, cancellationToken);
            var batches = await store.ScanAsync(layer.Layer.Dataset, cancellationToken);
            long ordinal = 0;
            foreach (var feature in batches.SelectMany(batch => batch.Features))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ordinal++;
                if (definition is not null && !MatchesDefinition(scheme!, layer.Dataset, definition, feature, ordinal))
                {
                    continue;
                }

                if (times?.GetValueOrDefault(layer.Layer.Id) is { } extent
                    && !FeatureQueryEngine.MatchesTime(feature, new EsriTimeExtent(extent.StartMs, extent.EndMs)))
                {
                    continue;
                }

                if (FeatureGeometry.Find(feature) is not { } geometry
                    || operations.Intersection(geometry, localQuery, cancellationToken).IsEmpty)
                {
                    continue;
                }

                var projected = returnGeometry ? Transform(geometry, identifyCrs, transforms, cancellationToken) : null;
                hits.Add(new IdentifyHit(layer, feature, projected));
            }
        }

        return hits;
    }

    /// <summary>
    /// Parses one layer's <c>layerDefs</c> clause. The clause was validated
    /// when the <c>layerDefs</c> object parsed, so its re-rendered form always
    /// parses; a failure here is still typed rather than silent.
    /// </summary>
    private static EsriFilterClause ParseLayerDef(int layerId, string where) =>
        EsriFilterClause.TryParse(where, out var clause, out var error) && clause is not null
            ? clause
            : throw EsriInteropException.Invalid($"'layerDefs' clause for layer {layerId} is not supported: {error}.");

    /// <summary>
    /// Applies one layer's definition to a feature, resolving the synthetic
    /// <c>OBJECTID</c> exactly as the query path does.
    /// </summary>
    private static bool MatchesDefinition(
        EsriObjectIdScheme scheme, DatasetDescription dataset, EsriFilterClause definition, Feature feature, long ordinal)
    {
        if (!scheme.TryResolve(feature, ordinal, out var objectId))
        {
            throw new EsriInteropException(
                EsriErrorCodes.ServerError,
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

    private sealed record IdentifyHit(MapLayerInfo Layer, Feature Feature, IGeometry? Geometry);
}
