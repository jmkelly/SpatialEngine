using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Maps engine metadata and the neutral <see cref="LayerStyle"/> onto the
/// MapServer resource shapes (spec §4.0/§4.2, ADR-0047). The renderer subset
/// is deliberately small: one simple renderer and a point/line/fill symbol.
/// Labels, class breaks and scale dependencies stay out of scope (M4 of
/// <c>map-service-plan.md</c>).
/// </summary>
internal static class EsriMapModel
{
    /// <summary>The MapServer capability string M0 actually serves: query and data, never <c>Map</c> (no export).</summary>
    public const string Capabilities = "Query,Data";

    /// <summary>Builds the MapServer root (spec §4.0).</summary>
    public static EsriMapServerRoot Root(
        Publication publication,
        IReadOnlyList<EsriMapLayerRef> layers,
        EsriExtentDto? extent,
        EsriSpatialReferenceDto? spatialReference,
        string units) =>
        new(
            10.0,
            "SpatialEngine Map Service",
            publication.Name,
            publication.Description,
            publication.Copyright ?? string.Empty,
            "JSON",
            Capabilities,
            EsriLayerModel.MaxRecordCount,
            units,
            spatialReference,
            extent,
            extent,
            layers,
            []);

    /// <summary>Builds a layer reference for the root and <c>layers</c> resources.</summary>
    public static EsriMapLayerRef Reference(int id, string name, string geometryType) =>
        new(id, name, "Feature Layer", geometryType, 0, 0);

    /// <summary>Builds the full layer metadata (spec §4.2) including <c>drawingInfo</c>.</summary>
    public static EsriMapLayer Layer(int id, string name, DatasetDescription dataset, LayerStyle? style) =>
        new(
            10.0,
            id,
            name,
            "Feature Layer",
            EsriLayerModel.GeometryType(dataset.GeometryType),
            EsriLayerModel.ObjectIdField,
            EsriLayerModel.FieldsOf(dataset),
            EsriLayerModel.ReadOnlyCapabilities,
            EsriLayerModel.MaxRecordCount,
            EsriLayerModel.SpatialReference(dataset.Srid),
            new EsriDrawingInfo(Renderer(EsriLayerModel.GeometryType(dataset.GeometryType), style)));

    /// <summary>Lowers a neutral style (or the default) into a single-symbol simple renderer.</summary>
    public static EsriRenderer Renderer(string esriGeometryType, LayerStyle? style)
    {
        var effective = style ?? LayerStyle.Default;
        var fill = Color(effective.Color, effective.Opacity);
        var stroke = Color(effective.Color, 1.0);
        if (esriGeometryType is "esriGeometryPolygon")
        {
            return new EsriRenderer("simple", new EsriFillSymbol(
                "esriSFS", "esriSFSSolid", fill, new EsriLineSymbol("esriSLS", "esriSLSSolid", stroke, effective.LineWidth)));
        }

        if (esriGeometryType is "esriGeometryPolyline")
        {
            return new EsriRenderer("simple", new EsriLineSymbol("esriSLS", "esriSLSSolid", fill, effective.LineWidth));
        }

        return new EsriRenderer("simple", new EsriPointSymbol(
            "esriSMS",
            "esriSMSCircle",
            fill,
            effective.Radius * 2,
            new EsriLineSymbol("esriSLS", "esriSLSSolid", stroke, effective.LineWidth)));
    }

    /// <summary>Parses <c>#rgb</c>/<c>#rrggbb</c> into an Esri <c>[r,g,b,a]</c> array.</summary>
    public static int[] Color(string hex, double opacity)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 3)
        {
            value = string.Concat(value.Select(c => new string(c, 2)));
        }

        var red = Convert.ToInt32(value[..2], 16);
        var green = Convert.ToInt32(value.Substring(2, 2), 16);
        var blue = Convert.ToInt32(value.Substring(4, 2), 16);
        var alpha = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        return [red, green, blue, alpha];
    }

    /// <summary>Maps an SRID onto the spec's unit name.</summary>
    public static string Units(int srid) => srid switch
    {
        4326 => "esriDecimalDegrees",
        3857 => "esriMeters",
        _ => "esriUnknownUnits",
    };
}

/// <summary>The <c>MapServer</c> root shape (spec §4.0).</summary>
internal sealed record EsriMapServerRoot(
    double CurrentVersion,
    string ServiceDescription,
    string MapName,
    string? Description,
    string CopyrightText,
    string SupportedQueryFormats,
    string Capabilities,
    int MaxRecordCount,
    string Units,
    EsriSpatialReferenceDto? SpatialReference,
    EsriExtentDto? InitialExtent,
    EsriExtentDto? FullExtent,
    IReadOnlyList<EsriMapLayerRef> Layers,
    IReadOnlyList<EsriMapLayerRef> Tables);

/// <summary>One layer reference under a MapServer resource.</summary>
internal sealed record EsriMapLayerRef(int Id, string Name, string Type, string GeometryType, int MinScale, int MaxScale);

/// <summary>One map layer's full metadata (spec §4.2).</summary>
internal sealed record EsriMapLayer(
    double CurrentVersion,
    int Id,
    string Name,
    string Type,
    string GeometryType,
    string ObjectIdField,
    IReadOnlyList<EsriField> Fields,
    string Capabilities,
    int MaxRecordCount,
    EsriSpatialReferenceDto? SpatialReference,
    EsriDrawingInfo DrawingInfo);

/// <summary>The <c>drawingInfo</c> envelope with one simple renderer (spec §12).</summary>
internal sealed record EsriDrawingInfo(EsriRenderer Renderer);

/// <summary>A simple renderer carrying one symbol.</summary>
internal sealed record EsriRenderer(string Type, object? Symbol);

/// <summary>A point marker symbol (spec §13).</summary>
internal sealed record EsriPointSymbol(string Type, string Style, int[] Color, double Size, EsriLineSymbol Outline);

/// <summary>A line stroke symbol.</summary>
internal sealed record EsriLineSymbol(string Type, string Style, int[] Color, double Width);

/// <summary>A polygon fill symbol (spec §13).</summary>
internal sealed record EsriFillSymbol(string Type, string Style, int[] Color, EsriLineSymbol Outline);

/// <summary>An Esri extent (spec §4.0).</summary>
internal sealed record EsriExtentDto(double Xmin, double Ymin, double Xmax, double Ymax, EsriSpatialReferenceDto? SpatialReference);
