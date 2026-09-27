using Spatial.Contracts;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The relationship read handler (spec §9.1.5, ADR-0077): one call that
/// negotiates <c>f=json</c>, maps every failure onto the Esri envelope and
/// hands the traversal its parameters. The relate/unrelate writes are not
/// here — they are edits and travel the edit routes' admin gate
/// (<see cref="FeatureEditEndpoints"/>).
/// </summary>
internal static class FeatureRelationshipEndpoints
{
    internal static async Task<IResult> QueryRelatedRecords(
        QueryRequest request,
        int layerId,
        IGeometryOperations operations,
        IGeometryRelations relations,
        ICoordinateTransforms transforms)
    {
        try
        {
            return await FeatureRelationshipEngine.QueryRelatedRecordsAsync(
                request, layerId, operations, relations, transforms, request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}
