using Microsoft.AspNetCore.Http;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The read-only Feature Server routes (spec §9, ADR-0035): the service
/// root and one layer's metadata. The Feature (object) resource and the
/// per-layer <c>query</c> live in <see cref="FeatureResourceEndpoints"/>,
/// editing in <see cref="FeatureEditEndpoints"/> and the write-model
/// operations in <see cref="FeatureWriteModelEndpoints"/>. Every
/// layer is resolved through <see cref="GeoServicesResolution"/> — an unknown
/// layer id is <c>not.found</c>, never silently dropped — and every failure
/// maps to the Esri error envelope.
/// </summary>
internal static class FeatureServerEndpoints
{
    public static void MapFeatureServer(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry)
    {
        group.MapMethods("/{service}/FeatureServer", ["GET", "POST"], (string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureServerRoot(catalog, registry, service, context, stores, cancellationToken));
        group.MapMethods("/{service}/FeatureServer/{layerId:int}", ["GET", "POST"], (string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureLayer(new FeatureLayerContext(catalog, registry, service, layerId, context, stores), cancellationToken));
        // The Feature (object) resource (spec §9.1.2): ArcGIS REST JS
        // getFeature reads `<layerId>/<objectId>` directly.
        FeatureResourceEndpoints.MapFeatureResources(group, catalog, registry);
    }

    private static async Task<IResult> FeatureServerRoot(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapServiceKind.FeatureServer, cancellationToken);
            var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
            var (spatial, tables) = await GeoServicesResolution.SplitTablesAsync(stores, resolved, layers, cancellationToken);
            return EsriJson.Value(FeatureService.Root(spatial, tables, GeoServicesResolution.IsEditable(stores, resolved.Store)));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> FeatureLayer(FeatureLayerContext request, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(request.Context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(request.Catalog, request.Registry, request.Service, "FeatureServer", MapServiceKind.FeatureServer, cancellationToken);
            var description = await GeoServicesResolution.DescribeAsync(request.Stores, resolved, request.LayerId, cancellationToken);
            var editable = GeoServicesResolution.IsEditable(request.Stores, resolved.Store) && EsriObjectIdScheme.For(description).SupportsEditing;
            return EsriJson.Value(FeatureService.Layer(
                request.LayerId,
                description,
                editable,
                EsriLayerModel.IsTable(description),
                GeoServicesResolution.HasAttachments(request.Stores, resolved.Store)));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

}

/// <summary>The resolved services of one Feature Service layer-metadata request.</summary>
internal sealed record FeatureLayerContext(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    string Service,
    int LayerId,
    HttpContext Context,
    IStoreRegistry Stores);
