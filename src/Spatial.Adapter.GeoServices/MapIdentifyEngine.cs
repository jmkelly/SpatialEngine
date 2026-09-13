using System.Globalization;
using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer <c>identify</c> operation (spec §4.0.5): given a point or
/// envelope, a pixel tolerance and a layer selection, it returns the features
/// that intersect the geometry across the selected layers. The identify
/// geometry is buffered by the tolerance (map units derived from
/// <c>mapExtent</c>/<c>imageDisplay</c>) and matched with the engine's
/// <see cref="IGeometryOperations"/> verbs; attributes and geometry are
/// written as Esri JSON.
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
        var hits = await MatchAsync(store, MapLayerSelection.Select(layers, parameters.Get("layers")), queryGeometry, identifyCrs, returnGeometry, operations, transforms, cancellationToken);
        return EsriJson.Write(writer => WriteResults(writer, hits, returnGeometry));
    }

    private static async Task<List<IdentifyHit>> MatchAsync(
        IFeatureStore store,
        IReadOnlyList<MapLayerInfo> layers,
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
            var layerCrs = EsriLayerModel.LayerCoordinateReference(layer.Dataset.Srid);
            var localQuery = Transform(queryGeometry, layerCrs, transforms, cancellationToken);
            var batches = await store.ScanAsync(layer.Layer.Dataset, cancellationToken);
            foreach (var feature in batches.SelectMany(batch => batch.Features))
            {
                cancellationToken.ThrowIfCancellationRequested();
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
        if (extent.Count < 4 || display.Count < 2 || display[0] <= 0)
        {
            return 0;
        }

        return Math.Abs(pixels) * ((extent[2] - extent[0]) / display[0]);
    }

    private static double ParseDouble(string? value, double fallback) =>
        string.IsNullOrWhiteSpace(value) || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? fallback
            : parsed;

    private sealed record IdentifyHit(MapLayerInfo Layer, Feature Feature, IGeometry? Geometry);
}
