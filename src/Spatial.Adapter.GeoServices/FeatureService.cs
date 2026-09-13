using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Feature Service (spec §9): the <c>FeatureServer</c> root, layer
/// metadata, <c>query</c> and the editing operations
/// (<c>addFeatures</c>, <c>updateFeatures</c>, <c>deleteFeatures</c>,
/// <c>applyEdits</c>). Features come from the engine's keyed stores; the
/// facade maps them to Esri JSON, applies the supported query/edit subset,
/// and never forwards client text as SQL. Editing is advertised and accepted
/// only for layers whose dataset has an integer identity column and whose
/// store implements <see cref="IFeatureEditStore"/> (ADR-0037); everything
/// else stays read-only. Partial updates and per-object deletes resolve
/// their targets through <see cref="IFeatureLookup"/> when the store
/// provides it, so the edit path does not scan the whole dataset (ADR-0038).
/// The query and edit paths live in <see cref="FeatureQueryEngine"/> and
/// <see cref="FeatureEditEngine"/>; this type is the thin protocol facade
/// (ADR-0040).
/// </summary>
internal static class FeatureService
{
    private const double CurrentVersion = 10.0;

    /// <summary>
    /// Builds the <c>FeatureServer</c> root (spec §9.0): spatial datasets
    /// under <c>layers</c>, datasets without a geometry field under
    /// <c>tables</c>. Both lists share the single layer/table id space, so
    /// ids stay stable whether or not tables exist.
    /// </summary>
    public static EsriFeatureServerRoot Root(IReadOnlyList<PublishedLayer> layers, IReadOnlyList<PublishedLayer> tables, bool editable)
    {
        var layerRefs = layers.Select(layer => EsriLayerModel.Reference(layer.Id, layer.Name)).ToArray();
        var tableRefs = tables.Select(table => EsriLayerModel.Reference(table.Id, table.Name, isTable: true)).ToArray();
        return new EsriFeatureServerRoot(
            CurrentVersion,
            "SpatialEngine Feature Service",
            false,
            EsriLayerModel.SupportedQueryFormats,
            editable ? EsriLayerModel.EditableCapabilities : EsriLayerModel.ReadOnlyCapabilities,
            EsriLayerModel.MaxRecordCount,
            layerRefs,
            tableRefs,
            EsriLayerModel.QueryCapabilities);
    }

    /// <summary>Builds one layer's metadata (spec §9.1).</summary>
    public static EsriLayer Layer(int layerId, DatasetDescription dataset, bool editable, bool isTable = false) =>
        EsriLayerModel.Describe(layerId, dataset, editable, isTable);

    /// <summary>Executes a query and writes the spec §9.1.4.3 response.</summary>
    public static Task<IResult> QueryAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriFeatureQuery query,
        IGeometryOperations operations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken) =>
        FeatureQueryEngine.QueryAsync(dataset, store, query, operations, transforms, cancellationToken);

    /// <summary>
    /// Reads one feature by its Esri <c>OBJECTID</c> (the Feature resource,
    /// spec §9.1.2) and writes the <c>{"feature": ...}</c> envelope. The
    /// object id is resolved exactly as <c>query</c> does so the resource
    /// agrees with <c>returnIdsOnly</c>.
    /// </summary>
    public static Task<IResult> FeatureAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        long objectId,
        EsriFeatureQuery query,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken) =>
        FeatureQueryEngine.FeatureAsync(dataset, store, objectId, query, transforms, cancellationToken);

    /// <summary>Executes the requested editing operation and writes its per-feature results.</summary>
    public static Task<IResult> EditsAsync(
        EsriEditOperation operation,
        DatasetDescription dataset,
        IFeatureStore store,
        IFeatureEditStore editStore,
        EsriEditRequest request,
        CoordinateReference? layerCrs,
        CancellationToken cancellationToken) =>
        FeatureEditEngine.EditsAsync(operation, dataset, store, editStore, request, layerCrs, cancellationToken);
}

/// <summary>The <c>returnIdsOnly</c> response (spec §9.1.4.4).</summary>
internal sealed record EsriObjectIdsResponse(string ObjectIdFieldName, IReadOnlyList<long> ObjectIds);

/// <summary>The <c>returnCountOnly</c> response (10.x addition).</summary>
internal sealed record EsriCountResponse(int Count);
