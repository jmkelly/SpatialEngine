using Spatial.Contracts;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The service-level <c>FeatureServer/query</c> handler (T-038, S1): reads
/// the request, plans the per-layer targets and hands them to the response
/// writer. Split out of <see cref="GeoServicesEndpoints"/> so the route facade
/// keeps only mapping; the per-layer operations live in
/// <see cref="FeatureLayerQueryHandlers"/> and the request planning in
/// <see cref="ServiceQueryPlan"/> (ADR-0040).
/// </summary>
internal static class FeatureQueryHandlers
{
    internal static async Task<IResult> FeatureServiceQuery(
        QueryRequest request, IGeometryOperations operations, IGeometryRelations relations, ICoordinateTransforms transforms)
    {
        try
        {
            var parameters = await LayerQuery.ReadParameters(request);
            var plan = await ServiceQueryPlan.BuildAsync(request, parameters, request.CancellationToken);
            var store = request.Stores.Features(plan.Store);
            return await FeatureResponseWriter.ServiceQueryAsync(
                plan.Targets, store, plan.Shared, operations, relations, transforms, request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}
