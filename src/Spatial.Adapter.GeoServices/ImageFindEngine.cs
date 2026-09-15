using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The ImageServer <c>find</c> operation: a literal text search over the
/// string fields of the raster catalog, mirroring the MapServer <c>find</c>
/// shape (spec §4.0.6). <c>contains</c> (default) or <c>startsWith</c> selects
/// the comparison; <c>searchFields</c> narrows the fields, otherwise every
/// string field is searched. The S3 11.2 oriented-imagery <c>find</c>
/// (<c>fromGeometry</c>/<c>toGeometry</c> camera workflow) needs sensor models
/// the engine does not have and is rejected by name (ADR-0054).
/// </summary>
internal static class ImageFindEngine
{
    public static IResult Find(
        RasterDatasetDescription description,
        IReadOnlyList<RasterCatalogItem> items,
        EsriRequestParameters parameters,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var searchText = parameters.Require("searchText");
        RejectOriented(parameters);
        AcceptLayer(parameters);
        var contains = parameters.GetBool("contains", true);
        var requested = ParseFields(parameters.Get("searchFields"));
        var returnGeometry = parameters.GetBool("returnGeometry", true);
        var outCrs = EsriValueParser.ParseSpatialReference(parameters.Get("sr"));
        var schema = description.CatalogSchema
            ?? throw GeoServicesErrors.Invalid($"Image Service '{description.Dataset}' does not include an accessible raster catalog.");
        var layerCrs = EsriLayerModel.LayerCoordinateReference(MapServerResources.SridOf(description.Raster.Crs));
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("results");
            writer.WriteStartArray();
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var feature = ImageService.Feature(item, schema);
                var matched = Match(feature, SearchFields(schema, requested), searchText, contains);
                if (matched is null)
                {
                    continue;
                }

                var geometry = GeometryOf(item, layerCrs, outCrs, transforms, returnGeometry, cancellationToken);
                WriteMatch(writer, description, feature, matched.Value, geometry);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    private static IGeometry? GeometryOf(
        RasterCatalogItem item,
        CoordinateReference? layerCrs,
        CoordinateReference? outCrs,
        ICoordinateTransforms transforms,
        bool returnGeometry,
        CancellationToken cancellationToken) =>
        returnGeometry
            ? Transform(item.Footprint, layerCrs, outCrs, transforms, cancellationToken)
            : null;

    private static void WriteMatch(
        Utf8JsonWriter writer,
        RasterDatasetDescription description,
        Feature feature,
        (string Field, string Value) matched,
        IGeometry? geometry)
    {
        writer.WriteStartObject();
        writer.WriteNumber("layerId", 0);
        writer.WriteString("layerName", description.Name);
        writer.WriteString("foundFieldName", matched.Field);
        writer.WriteString("value", matched.Value);
        writer.WritePropertyName("attributes");
        writer.WriteStartObject();
        MapFeatures.WriteAttributes(writer, feature);
        writer.WriteEndObject();
        writer.WriteString("geometryType", "esriGeometryPolygon");
        if (geometry is not null)
        {
            writer.WritePropertyName("geometry");
            EsriGeometryCodec.Write(writer, geometry);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// The oriented-imagery inspection workflow needs per-item sensor models;
    /// silently ignoring its camera geometry would rank the wrong images.
    /// </summary>
    private static void RejectOriented(EsriRequestParameters parameters)
    {
        if (parameters.Has("fromGeometry") || parameters.Has("toGeometry"))
        {
            throw GeoServicesErrors.Invalid(
                "The 'fromGeometry'/'toGeometry' oriented-imagery workflow is not supported: catalog items carry no sensor models. Use 'searchText' for the catalog text search.");
        }
    }

    /// <summary>The catalog is the service's only layer: absent means all of it, anything but 0 is invalid.</summary>
    private static void AcceptLayer(EsriRequestParameters parameters)
    {
        var layers = parameters.Get("layers");
        if (string.IsNullOrWhiteSpace(layers)
            || string.Equals(layers.Trim(), "0", StringComparison.Ordinal)
            || string.Equals(layers.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw GeoServicesErrors.Invalid($"The 'layers' parameter names an unknown layer: the raster catalog is layer 0.");
    }

    private static (string Field, string Value)? Match(Feature feature, IReadOnlyList<string> fields, string searchText, bool contains)
    {
        foreach (var field in fields)
        {
            if (MapFeatures.StringValue(feature, field) is not { } value)
            {
                continue;
            }

            var hit = contains
                ? value.Contains(searchText, StringComparison.OrdinalIgnoreCase)
                : value.StartsWith(searchText, StringComparison.OrdinalIgnoreCase);
            if (hit)
            {
                return (field, value);
            }
        }

        return null;
    }

    private static List<string> SearchFields(FeatureSchema schema, IReadOnlyList<string>? requested)
    {
        var fields = new List<string>();
        if (requested is null)
        {
            for (var i = 0; i < schema.Count; i++)
            {
                if (schema[i].Kind == AttributeKind.String)
                {
                    fields.Add(schema[i].Name);
                }
            }

            return fields;
        }

        foreach (var name in requested)
        {
            var index = schema.IndexOf(name);
            if (index >= 0 && schema[index].Kind == AttributeKind.String)
            {
                fields.Add(name);
            }
        }

        return fields;
    }

    private static string[]? ParseFields(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static IGeometry? Transform(IGeometry? geometry, CoordinateReference? source, CoordinateReference? target, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
        geometry is not null && target is { } to && source is { } from && from != to
            ? transforms.Transform(geometry, from.ToString(), to.ToString(), cancellationToken)
            : geometry;
}
