using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The per-layer Feature query operations: <c>generateRenderer</c> (S1
/// §12.11), <c>validateSQL</c> (S1 §12.14) and the honestly rejected
/// aggregation extensions. Split out of <see cref="FeatureQueryHandlers"/>
/// so the service-level query and the per-layer operations carry their own
/// fan-out; the four-step preamble they share lives in
/// <see cref="LayerQuery"/>.
/// </summary>
internal static class FeatureLayerQueryHandlers
{
    internal static async Task<IResult> FeatureGenerateRenderer(QueryRequest request, int layerId)
    {
        try
        {
            var parameters = await LayerQuery.ReadParameters(request);
            var scope = await LayerQuery.OpenAsync(request, layerId);
            var renderer = await MapGenerateRenderer.GenerateAsync(
                request.Stores.Features(scope.Store), scope.Description, parameters.Get("classificationDef"), parameters.Get("where"), request.CancellationToken);
            return EsriJson.Value(new EsriGenerateRendererResponse(renderer));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static async Task<IResult> FeatureValidateSql(QueryRequest request, int layerId)
    {
        try
        {
            var parameters = await LayerQuery.ReadParameters(request);
            var scope = await LayerQuery.OpenAsync(request, layerId);
            return EsriJson.Value(Adapter.GeoServices.FeatureValidateSql.Validate(
                scope.Description, parameters.Get("sql"), parameters.Get("sqlType")));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static async Task<IResult> UnsupportedLayerOperation(
        QueryRequest request, int layerId, string operation, string guidance)
    {
        try
        {
            var parameters = await LayerQuery.ReadParameters(request);
            _ = await LayerQuery.OpenAsync(request, layerId);
            throw GeoServicesErrors.Invalid($"The '{operation}' operation is not supported on service '{request.Service}': {guidance}");
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}

/// <summary>
/// The four-step preamble every per-layer Feature query operation repeats:
/// read and format-check the request parameters, resolve the published
/// service, then describe the addressed layer. Naming it once keeps the
/// handlers to what each operation actually varies on.
/// </summary>
internal static class LayerQuery
{
    /// <summary>Reads the Esri request parameters and rejects an unsupported <c>f</c> format.</summary>
    public static Task<EsriRequestParameters> ReadParameters(QueryRequest request) =>
        ReadParametersAsync(request.Context, request.CancellationToken);

    /// <inheritdoc cref="ReadParameters(QueryRequest)"/>
    public static Task<EsriRequestParameters> ReadParameters(AttachmentRequest request) =>
        ReadParametersAsync(request.Context, request.CancellationToken);

    private static async Task<EsriRequestParameters> ReadParametersAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
        EsriFormat.Ensure(parameters.Get("f"));
        return parameters;
    }

    /// <summary>Resolves the service and describes the addressed layer, or fails as <c>not.found</c>.</summary>
    public static Task<LayerScope> OpenAsync(QueryRequest request, int layerId) =>
        OpenAsync(request.Catalog, request.Registry, request.Service, request.Stores, layerId, request.CancellationToken);

    /// <inheritdoc cref="OpenAsync(QueryRequest, int)"/>
    public static async Task<LayerScope> OpenAsync(AttachmentRequest request, int layerId) =>
        await OpenAsync(request.Catalog, request.Registry, request.Service, request.Stores, layerId, request.CancellationToken);

    private static async Task<LayerScope> OpenAsync(
        GeoServicesCatalog catalog,
        IMapRegistry registry,
        string service,
        IStoreRegistry stores,
        int layerId,
        CancellationToken cancellationToken)
    {
        var resolved = await GeoServicesResolution.ResolveServiceAsync(
            catalog, registry, service, "FeatureServer", MapServiceKind.FeatureServer, cancellationToken);
        var description = await GeoServicesResolution.DescribeAsync(stores, resolved, layerId, cancellationToken);
        return new LayerScope(resolved.Store, description);
    }

    /// <summary>One layer addressed by a per-layer query operation: its store key and its catalogue description.</summary>
    internal sealed record LayerScope(string Store, DatasetDescription Description);
}
