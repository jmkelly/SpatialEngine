using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Applying one Feature Service edit request (spec §9.1.6–§9.1.9,
/// ADR-0037): resolve the service, describe the layer, take the store's
/// keyed editing face (a read-only store fails as typed
/// <c>invalid.arguments</c>) and hand the parsed edits to
/// <see cref="FeatureService"/>. Split out of
/// <see cref="FeatureEditEndpoints"/>, which owns the routes and the admin
/// gate, so the request pipeline sits with the projection it drives.
/// </summary>
internal static class FeatureEditRequests
{
    /// <summary>Runs one edit operation over one layer and returns its Esri result.</summary>
    public static async Task<IResult> ApplyAsync(
        FeatureEditContext request, EsriRequestParameters parameters, CancellationToken cancellationToken)
    {
        EsriEditRequest.RejectUnsupported(parameters);
        var resolved = await GeoServicesResolution.ResolveServiceAsync(
            request.Catalog, request.Registry, request.Service, "FeatureServer", MapServiceKind.FeatureServer, cancellationToken);
        var description = await GeoServicesResolution.DescribeAsync(request.Stores, resolved, request.LayerId, cancellationToken);
        var store = request.Stores.Features(resolved.Store);
        var editStore = request.Stores.EditStore(resolved.Store)
            ?? throw GeoServicesErrors.Invalid(
                $"Service '{request.Service}' is read-only; it exposes no feature-editing face.");

        return await FeatureService.EditsAsync(
            new FeatureEditEngine.EditInvocation(
                request.Operation,
                description,
                store,
                editStore,
                ParseEdits(request.Operation, parameters),
                EsriLayerModel.LayerCoordinateReference(description.Srid)),
            cancellationToken);
    }

    /// <summary>The edit-operation parsers; an unrecognised operation parses the combined <c>apply</c> form.</summary>
    private static readonly Dictionary<EsriEditOperation, Func<EsriRequestParameters, EsriEditRequest>> EditParsers =
        new()
        {
            [EsriEditOperation.Add] = EsriEditRequest.ParseAdd,
            [EsriEditOperation.Update] = EsriEditRequest.ParseUpdate,
            [EsriEditOperation.Delete] = EsriEditRequest.ParseDelete,
            [EsriEditOperation.Apply] = EsriEditRequest.ParseApply,
        };

    private static EsriEditRequest ParseEdits(EsriEditOperation operation, EsriRequestParameters parameters) =>
        EditParsers.TryGetValue(operation, out var parse) ? parse(parameters) : EsriEditRequest.ParseApply(parameters);
}
