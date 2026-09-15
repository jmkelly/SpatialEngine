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
/// surface served on the <c>IFeatureAttachmentStore</c> capability (T-061,
/// ADR-0066): reads follow feature-query auth (public), writes require the
/// single admin token (ADR-0065 §3).
/// </summary>
public static partial class GeoServicesEndpoints
{
    internal static void MapFeatureOps(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry, string? adminToken = null)
    {
        group.MapMethods("/{service}/FeatureServer/query", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores,
            IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            FeatureQueryHandlers.FeatureServiceQuery(catalog, registry, service, context, stores, operations, transforms, cancellationToken));

        group.MapMethods("/{service}/FeatureServer/{layerId:int}/generateRenderer", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureQueryHandlers.FeatureGenerateRenderer(catalog, registry, service, layerId, context, stores, cancellationToken));

        group.MapMethods("/{service}/FeatureServer/{layerId:int}/validateSQL", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureQueryHandlers.FeatureValidateSql(catalog, registry, service, layerId, context, stores, cancellationToken));

        // Aggregation extensions without an engine model (ADR-0058 §4):
        // mounted so clients get a typed invalid-arguments failure naming
        // the served alternative instead of a bare 404.
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/queryBins", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureQueryHandlers.UnsupportedLayerOperation(catalog, registry, service, layerId, context, stores,
                "queryBins",
                "binned aggregation has no engine model; use 'query' with 'outStatistics' and 'groupByFieldsForStatistics' instead.",
                cancellationToken));
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/queryTopFeatures", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureQueryHandlers.UnsupportedLayerOperation(catalog, registry, service, layerId, context, stores,
                "queryTopFeatures",
                "top-N aggregation has no engine model; use 'query' with 'orderByFields' and 'resultRecordCount' instead.",
                cancellationToken));
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/queryAnalytic", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureQueryHandlers.UnsupportedLayerOperation(catalog, registry, service, layerId, context, stores,
                "queryAnalytic",
                "analytic aggregation has no engine model; use 'query' with 'outStatistics' instead.",
                cancellationToken));

        // Attachments (T-061, ADR-0066): reads are served on the store's
        // attachment face (public, like the features they annotate),
        // writes require the single admin token (ADR-0065 §3). Layers whose
        // store exposes no face keep the honest surface: empty reads
        // and typed write rejects naming the missing face.
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/queryAttachments", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureAttachmentHandlers.FeatureQueryAttachments(catalog, registry, service, layerId, context, stores, cancellationToken));
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/{objectId:long}/attachments", ["GET", "POST"], (
            string service, int layerId, long objectId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureAttachmentHandlers.FeatureAttachmentInfos(catalog, registry, service, layerId, objectId, context, stores, cancellationToken));
        group.MapGet("/{service}/FeatureServer/{layerId:int}/{objectId:long}/attachments/{attachmentId:long}", (
            string service, int layerId, long objectId, long attachmentId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureAttachmentHandlers.FeatureAttachmentContent(catalog, registry, service, layerId, objectId, attachmentId, context, stores, cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/{objectId:long}/addAttachment", (
            string service, int layerId, long objectId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureAttachmentHandlers.FeatureAddAttachment(catalog, registry, service, layerId, objectId, context, stores, adminToken, cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/{objectId:long}/deleteAttachments", (
            string service, int layerId, long objectId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureAttachmentHandlers.FeatureDeleteAttachments(catalog, registry, service, layerId, objectId, context, stores, adminToken, cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/{objectId:long}/updateAttachment", (
            string service, int layerId, long objectId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureAttachmentHandlers.FeatureUpdateAttachment(catalog, registry, service, layerId, objectId, context, stores, adminToken, cancellationToken));
    }

    /// <summary>
    /// The service-level query (S1 query-feature-service/): the shared
    /// parameters apply to every queried layer, <c>layerDefs</c> narrows
    /// individual layers, and the response is one feature set, count, or id
    /// list per layer.
    /// </summary>

    /// <summary>
    /// Selects the service-query layers: the <c>layerDefs</c> ids when
    /// present (unknown ids are <c>not.found</c>, never silently dropped),
    /// otherwise every layer and table in id order.
    /// </summary>

    /// <summary>
    /// The Feature Service per-layer <c>generateRenderer</c>: the same
    /// server-side classification the MapServer serves (T-039,
    /// ADR-0055), reused rather than duplicated. The feature write-model
    /// track (T-038) mounts this route; the implementation stays the single
    /// <see cref="MapGenerateRenderer"/> classifier.
    /// </summary>

    /// <summary>The layer-level <c>validateSQL</c> (S4): validates the <c>sql</c> WHERE clause, never runs it.</summary>

    /// <summary>
    /// An aggregation extension with no engine model (ADR-0058 §4): the
    /// layer must exist (unknown ids stay <c>not.found</c>), then the
    /// operation fails as typed <c>invalid.arguments</c> naming the served
    /// alternative.
    /// </summary>

    /// <summary>The layer-level <c>queryAttachments</c> (S4): one group per requested feature over the store's capability.</summary>

    /// <summary>The per-feature <c>attachments</c> resource: the stored attachment infos for one feature.</summary>

    /// <summary>The per-attachment content resource: the stored bytes with their content type.</summary>

    /// <summary>The per-feature <c>addAttachment</c>: an admin-gated multipart upload stored on the capability.</summary>

    /// <summary>The per-feature <c>deleteAttachments</c>: an admin-gated batch delete with per-id results.</summary>

    /// <summary>The per-feature <c>updateAttachment</c>: an admin-gated multipart replacement keeping the identity.</summary>

    /// <summary>
    /// Resolves one layer's dataset for serving: the published layer and its
    /// catalogue description. An unknown layer id is <c>not.found</c>, never
    /// silently dropped.
    /// </summary>


    /// <summary>
    /// Gates an attachment write on the single admin token (ADR-0065 §3),
    /// exactly like the Esri admin projection gate: unconfigured means
    /// unavailable, a missing token is required, a wrong token is invalid.
    /// The token travels as a Bearer header or a <c>token</c> parameter.
    /// </summary>



    /// <summary>
    /// Reads the multipart upload of an attachment write: the file part named
    /// <c>attachment</c> (the single file part when unnamed). Anything else
    /// is a typed <c>invalid.arguments</c> failure naming the expectation.
    /// </summary>
}

