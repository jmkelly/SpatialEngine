using Spatial.Contracts;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Geometry Service routes (spec §5): the server metadata resource and
/// the per-operation dispatch. Kept separate from the feature/map/image
/// endpoints so the catalog facade stays a thin composition root.
/// </summary>
internal static class GeometryServerEndpoints
{
    internal static void MapGeometryServer(RouteGroupBuilder group)
    {
        group.MapMethods("/Geometry/GeometryServer", ["GET", "POST"], (HttpContext context, CancellationToken cancellationToken) =>
            GeometryServerInfo(context, cancellationToken));
        group.MapMethods("/Geometry/GeometryServer/{operation}", ["GET", "POST"], GeometryOperation);
    }

    private static async Task<IResult> GeometryServerInfo(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            return GeometryService.Info();
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> GeometryOperation(
        HttpContext context,
        string operation,
        IGeometryOperations geometry,
        IGeometryMeasures measures,
        IGeometryProcessing processing,
        IGeometryRelations relations,
        ICoordinateTransforms transforms,
        ICrsDirectory catalogue,
        CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var capabilities = new GeometryServiceCapabilities(geometry, measures, processing, relations, transforms, catalogue);
            return GeometryService.Dispatch(operation, parameters, capabilities, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}
