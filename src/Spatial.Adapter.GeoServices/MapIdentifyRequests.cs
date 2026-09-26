using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// What one identify request resolves to before it is parsed: the store
/// and the engine verbs behind it, the queried layers and the map's CRS.
/// Grouping them keeps the request and the match pipeline inside the
/// parameter budget.
/// </summary>
internal sealed record IdentifyRequest(
    IFeatureStore Store,
    IReadOnlyList<MapLayerInfo> Layers,
    int MapSrid,
    IGeometryOperations Operations,
    ICoordinateTransforms Transforms);

/// <summary>The engine verbs the match pipeline needs, projected from the request.</summary>
internal sealed record IdentifyServices(IFeatureStore Store, IGeometryOperations Operations, ICoordinateTransforms Transforms)
{
    public IdentifyServices(IdentifyRequest request)
        : this(request.Store, request.Operations, request.Transforms)
    {
    }
}

/// <summary>The per-request identify selection: layer filters, the buffered query geometry and its result shape.</summary>
internal sealed record IdentifyQuery(
    IReadOnlyDictionary<int, string>? LayerDefs,
    IReadOnlyDictionary<int, MapTimeExtent>? Times,
    IGeometry QueryGeometry,
    CoordinateReference? IdentifyCrs,
    bool ReturnGeometry);

/// <summary>One feature the identify geometry intersected, with the geometry projected to <c>sr</c> when it was asked for.</summary>
internal sealed record IdentifyHit(MapLayerInfo Layer, Feature Feature, IGeometry? Geometry);
