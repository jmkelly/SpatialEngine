using Microsoft.AspNetCore.Http;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The two Feature Server read routes that carry an <c>outSR</c> projection:
/// the Feature (object) resource (<c>&lt;layerId&gt;/&lt;objectId&gt;</c>,
/// spec §9.1.2) and the per-layer <c>query</c>. Both parse the Esri query
/// object, resolve the layer and project the result with the request's
/// coordinate transforms. Split out of <see cref="FeatureServerEndpoints"/>,
/// which owns the service root and layer metadata.
/// </summary>
internal static class FeatureResourceEndpoints
{
    public static void MapFeatureResources(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry)
    {
        // The Feature (object) resource (spec §9.1.2): ArcGIS REST JS
        // getFeature reads `<layerId>/<objectId>` directly.
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/{objectId:long}", ["GET", "POST"], (
            string service, int layerId, long objectId, HttpContext context, IStoreRegistry stores,
            ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            FeatureResource(new FeatureResourceContext(catalog, registry, service, layerId, objectId, context, stores, transforms), cancellationToken));

        group.MapMethods("/{service}/FeatureServer/{layerId:int}/query", ["GET", "POST"], (
            HttpContext context,
            string service,
            int layerId,
            IStoreRegistry stores,
            IGeometryOperations operations,
            ICoordinateTransforms transforms,
            CancellationToken cancellationToken) =>
            FeatureQuery(
                new FeatureQueryContext(catalog, registry, context, service, layerId, stores, operations, transforms),
                cancellationToken));
    }

    private static async Task<IResult> FeatureResource(FeatureResourceContext request, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(request.Context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(request.Catalog, request.Registry, request.Service, "FeatureServer", MapServiceKind.FeatureServer, cancellationToken);
            var description = await GeoServicesResolution.DescribeAsync(request.Stores, resolved, request.LayerId, cancellationToken);
            var query = EsriFeatureQuery.Parse(parameters, EsriLayerModel.LayerCoordinateReference(description.Srid));
            var store = request.Stores.Features(resolved.Store);
            return await FeatureService.FeatureAsync(description, store, request.ObjectId, query, request.Transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> FeatureQuery(FeatureQueryContext request, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(request.Context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(request.Catalog, request.Registry, request.Service, "FeatureServer", MapServiceKind.FeatureServer, cancellationToken);
            var description = await GeoServicesResolution.DescribeAsync(request.Stores, resolved, request.LayerId, cancellationToken);
            var query = EsriFeatureQuery.Parse(parameters, EsriLayerModel.LayerCoordinateReference(description.Srid));
            var store = request.Stores.Features(resolved.Store);
            return await FeatureService.QueryAsync(description, store, query, request.Operations, request.Transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}

/// <summary>The resolved services of one Feature (object) resource request.</summary>
internal sealed record FeatureResourceContext(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    string Service,
    int LayerId,
    long ObjectId,
    HttpContext Context,
    IStoreRegistry Stores,
    ICoordinateTransforms Transforms);

/// <summary>The resolved services of one Feature Service query request.</summary>
internal sealed record FeatureQueryContext(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    HttpContext Context,
    string Service,
    int LayerId,
    IStoreRegistry Stores,
    IGeometryOperations Operations,
    ICoordinateTransforms Transforms);
