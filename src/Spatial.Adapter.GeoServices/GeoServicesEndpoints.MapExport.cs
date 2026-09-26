using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Map Server export route (spec §4.0.4, ADR-0048). The tile route
/// lives with <see cref="MapServerTileEndpoints"/> so this facade keeps
/// only the export fan-out; the export itself is planned by
/// <see cref="MapExportPlanner"/> and answered by
/// <see cref="MapExportResult"/>, leaving this type the routing and the
/// Esri error envelope.
/// </summary>
internal static class MapExportEndpoints
{
    internal static void MapExportRoutes(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry)
    {
        group.MapMethods("/{service}/MapServer/export", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores, IMapRenderer renderer,
            ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            MapExport(new ExportRoute(catalog, registry, service, context, stores, renderer, transforms, cancellationToken)));
        MapServerTileEndpoints.MapTileRoutes(group, catalog, registry);
    }

    /// <summary>One export request's seams: the published service, its stores and the rendering stack.</summary>
    private sealed record ExportRoute(
        GeoServicesCatalog Catalog,
        IMapRegistry Registry,
        string Service,
        HttpContext Context,
        IStoreRegistry Stores,
        IMapRenderer Renderer,
        ICoordinateTransforms Transforms,
        CancellationToken CancellationToken);

    private static async Task<IResult> MapExport(ExportRoute route)
    {
        var (catalog, registry, service, context, stores, renderer, transforms, cancellationToken) = route;
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            var layers = await MapExportLayers.ResolveAsync(catalog, registry, stores, service, parameters, cancellationToken);
            var plan = await MapExportPlanner.CreateAsync(layers, stores, service, parameters, transforms, cancellationToken);
            if (string.Equals(parameters.Get("f"), "image", StringComparison.OrdinalIgnoreCase))
            {
                var request = new MapRenderRequest(
                    plan.Viewport, plan.Style, plan.Sources, null, plan.Format, 90, null,
                    parameters.GetBool("transparent", true), 1.0);
                return MapExportResult.Image(context, await renderer.RenderAsync(request, cancellationToken));
            }

            return MapExportResult.Metadata(context, plan, MapRenderParameters.Dpi(parameters.Get("dpi")));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}
