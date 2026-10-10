using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer export request plan (spec §4.0.4, ADR-0048): frame the
/// <c>bbox</c> in <c>bboxSR</c>/<c>imageSR</c> at the requested
/// <c>size</c>/<c>format</c> and compose the render request the renderer
/// serves from the layers <see cref="MapExportLayers"/> selected. Split out
/// of <see cref="MapExportEndpoints"/> — which only chooses the response
/// shape — so the projection chain every export repeats is named once and
/// stays independent of the route. The selected layers arrive already
/// resolved from <see cref="MapExportLayers"/>.
/// </summary>
internal static class MapExportPlanner
{
    /// <summary>Plans one export: the selected layers, their time window and the framed viewport to render.</summary>
    public static async Task<MapExportPlan> CreateAsync(
        MapExportLayers layers,
        IStoreRegistry stores,
        string service,
        EsriRequestParameters parameters,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var bbox = MapRenderParameters.ParseBbox(parameters.Get("bbox"));
        var (width, height) = MapRenderParameters.ParseSize(parameters.Get("size"));
        var bboxCrs = EsriValueParser.ParseSpatialReference(parameters.Get("bboxSR"))
            ?? await MapLayerCrs.ResolveAsync(stores, layers.Resolved, layers.Selected, cancellationToken);
        var imageCrs = EsriValueParser.ParseSpatialReference(parameters.Get("imageSR")) ?? bboxCrs;
        var framed = MapRenderParameters.FitExtent(
            MapRenderEngine.Project(bbox, bboxCrs, imageCrs ?? bboxCrs, transforms, cancellationToken),
            width,
            height);
        var viewport = new RasterViewport(
            framed,
            width,
            height,
            (imageCrs ?? bboxCrs)?.ToString() ?? "EPSG:4326");
        return new MapExportPlan(
            viewport,
            MapRenderEngine.Style(service, layers.Selected),
            MapRenderEngine.Sources(
                stores, layers.Resolved.Store, layers.Selected,
                MapRenderParameters.ParseLayerDefs(parameters.Get("layerDefs")), layers.Times),
            MapRenderParameters.ParseFormat(parameters.Get("format")),
            width,
            height,
            imageCrs);
    }
}
