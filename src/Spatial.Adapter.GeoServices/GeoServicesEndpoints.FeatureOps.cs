using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Feature write-model operations (T-038, ADR-0058): the service-level
/// <c>FeatureServer/query</c> (S1), the per-layer <c>generateRenderer</c>
/// (reusing the T-039 <see cref="MapGenerateRenderer"/> classification
/// rather than duplicating it), <c>validateSQL</c> (S4), the honestly
/// rejected aggregation extensions (<c>queryBins</c>,
/// <c>queryTopFeatures</c>, <c>queryAnalytic</c>), and the attachment
/// surface (empty reads, typed write rejects until an attachment store
/// lands).
/// </summary>
public static partial class GeoServicesEndpoints
{
    internal static void MapFeatureOps(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry)
    {
        group.MapMethods("/{service}/FeatureServer/query", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores,
            IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            FeatureServiceQuery(catalog, registry, service, context, stores, operations, transforms, cancellationToken));

        group.MapMethods("/{service}/FeatureServer/{layerId:int}/generateRenderer", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureGenerateRenderer(catalog, registry, service, layerId, context, stores, cancellationToken));

        group.MapMethods("/{service}/FeatureServer/{layerId:int}/validateSQL", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureValidateSql(catalog, registry, service, layerId, context, stores, cancellationToken));

        // Aggregation extensions without an engine model (ADR-0058 §4):
        // mounted so clients get a typed invalid-arguments failure naming
        // the served alternative instead of a bare 404.
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/queryBins", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            UnsupportedLayerOperation(catalog, registry, service, layerId, context, stores,
                "queryBins",
                "binned aggregation has no engine model; use 'query' with 'outStatistics' and 'groupByFieldsForStatistics' instead.",
                cancellationToken));
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/queryTopFeatures", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            UnsupportedLayerOperation(catalog, registry, service, layerId, context, stores,
                "queryTopFeatures",
                "top-N aggregation has no engine model; use 'query' with 'orderByFields' and 'resultRecordCount' instead.",
                cancellationToken));
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/queryAnalytic", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            UnsupportedLayerOperation(catalog, registry, service, layerId, context, stores,
                "queryAnalytic",
                "analytic aggregation has no engine model; use 'query' with 'outStatistics' instead.",
                cancellationToken));

        // Attachments (ADR-0058 §5): reads report the truthful empty set,
        // writes fail as typed invalid-arguments until an attachment store
        // lands. The layer never advertises hasAttachments.
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/queryAttachments", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureQueryAttachments(catalog, registry, service, layerId, context, stores, cancellationToken));
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/{objectId:long}/attachments", ["GET", "POST"], (
            string service, int layerId, long objectId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureAttachmentInfos(catalog, registry, service, layerId, context, stores, cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/{objectId:long}/addAttachment", (
            string service, int layerId, long objectId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureAttachmentWrite(catalog, registry, service, layerId, context, stores, "addAttachment", cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/{objectId:long}/deleteAttachments", (
            string service, int layerId, long objectId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureAttachmentWrite(catalog, registry, service, layerId, context, stores, "deleteAttachments", cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/{objectId:long}/updateAttachment", (
            string service, int layerId, long objectId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureAttachmentWrite(catalog, registry, service, layerId, context, stores, "updateAttachment", cancellationToken));
    }

    /// <summary>
    /// The service-level query (S1 query-feature-service/): the shared
    /// parameters apply to every queried layer, <c>layerDefs</c> narrows
    /// individual layers, and the response is one feature set, count, or id
    /// list per layer.
    /// </summary>
    private static async Task<IResult> FeatureServiceQuery(
        GeoServicesCatalog catalog,
        IMapRegistry registry,
        string service,
        HttpContext context,
        IStoreRegistry stores,
        IGeometryOperations operations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var layers = await ListLayersAsync(stores, resolved, cancellationToken);
            var defs = Adapter.GeoServices.FeatureServiceQuery.ParseLayerDefs(parameters.Get("layerDefs"));
            var selected = SelectServiceLayers(layers, defs, service);
            var catalogue = stores.Catalogue(resolved.Store);
            var descriptions = new List<(PublishedLayer Layer, DatasetDescription Description)>(selected.Count);
            Spatial.Core.Geometry.CoordinateReference? fallback = null;
            foreach (var layer in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var description = await catalogue.DescribeAsync(layer.Dataset, cancellationToken);
                fallback ??= EsriLayerModel.LayerCoordinateReference(description.Srid);
                descriptions.Add((layer, description));
            }

            var shared = EsriFeatureQuery.Parse(parameters, fallback);
            Adapter.GeoServices.FeatureServiceQuery.RejectLayerOnlyShapes(shared);
            var targets = descriptions
                .Select(entry => new ServiceLayerQuery(
                    entry.Layer.Id,
                    entry.Description,
                    Adapter.GeoServices.FeatureServiceQuery.ForLayer(
                        shared, defs.GetValueOrDefault(entry.Layer.Id)),
                    EsriLayerModel.IsTable(entry.Description)))
                .ToArray();
            var store = stores.Features(resolved.Store);
            return await FeatureQueryEngine.ServiceQueryAsync(targets, store, shared, operations, transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// Selects the service-query layers: the <c>layerDefs</c> ids when
    /// present (unknown ids are <c>not.found</c>, never silently dropped),
    /// otherwise every layer and table in id order.
    /// </summary>
    private static IReadOnlyList<PublishedLayer> SelectServiceLayers(
        IReadOnlyList<PublishedLayer> layers, IReadOnlyDictionary<int, Adapter.GeoServices.FeatureServiceQuery.LayerDef> defs, string service)
    {
        if (defs.Count == 0)
        {
            return layers;
        }

        var known = layers.ToDictionary(layer => layer.Id);
        var selected = new List<PublishedLayer>(defs.Count);
        foreach (var id in defs.Keys.OrderBy(id => id))
        {
            if (!known.TryGetValue(id, out var layer))
            {
                throw new EsriInteropException(EsriErrorCodes.NotFound, $"Layer {id} does not exist in service '{service}'.");
            }

            selected.Add(layer);
        }

        return selected;
    }

    /// <summary>
    /// The Feature Service per-layer <c>generateRenderer</c>: the same
    /// server-side classification the MapServer serves (T-039,
    /// ADR-0055), reused rather than duplicated. The feature write-model
    /// track (T-038) mounts this route; the implementation stays the single
    /// <see cref="MapGenerateRenderer"/> classifier.
    /// </summary>
    private static async Task<IResult> FeatureGenerateRenderer(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var description = await DescribeAsync(stores, resolved, layerId, cancellationToken);
            var renderer = await MapGenerateRenderer.GenerateAsync(
                stores.Features(resolved.Store), description, parameters.Get("classificationDef"), parameters.Get("where"), cancellationToken);
            return EsriJson.Value(new EsriGenerateRendererResponse(renderer));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The layer-level <c>validateSQL</c> (S4): validates the <c>sql</c> WHERE clause, never runs it.</summary>
    private static async Task<IResult> FeatureValidateSql(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var description = await DescribeAsync(stores, resolved, layerId, cancellationToken);
            return EsriJson.Value(Adapter.GeoServices.FeatureValidateSql.Validate(
                description, parameters.Get("sql"), parameters.Get("sqlType")));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// An aggregation extension with no engine model (ADR-0058 §4): the
    /// layer must exist (unknown ids stay <c>not.found</c>), then the
    /// operation fails as typed <c>invalid.arguments</c> naming the served
    /// alternative.
    /// </summary>
    private static async Task<IResult> UnsupportedLayerOperation(
        GeoServicesCatalog catalog,
        IMapRegistry registry,
        string service,
        int layerId,
        HttpContext context,
        IStoreRegistry stores,
        string operation,
        string guidance,
        CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            _ = await DescribeAsync(stores, resolved, layerId, cancellationToken);
            throw EsriInteropException.Invalid($"The '{operation}' operation is not supported on service '{service}': {guidance}");
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The layer-level <c>queryAttachments</c>: the truthful empty set (ADR-0058 §5).</summary>
    private static async Task<IResult> FeatureQueryAttachments(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            _ = await DescribeAsync(stores, resolved, layerId, cancellationToken);
            return FeatureAttachments.Empty();
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The per-feature <c>attachments</c> resource: the truthful empty set (ADR-0058 §5).</summary>
    private static async Task<IResult> FeatureAttachmentInfos(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            _ = await DescribeAsync(stores, resolved, layerId, cancellationToken);
            return FeatureAttachments.Empty();
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>An attachment write: the layer must exist, then the write is rejected (ADR-0058 §5).</summary>
    private static async Task<IResult> FeatureAttachmentWrite(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId,
        HttpContext context, IStoreRegistry stores, string operation, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            _ = await DescribeAsync(stores, resolved, layerId, cancellationToken);
            throw FeatureAttachments.WriteError(operation);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}
