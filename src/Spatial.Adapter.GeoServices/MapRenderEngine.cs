using System.Security.Cryptography;
using System.Text;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer render bridge (spec §4.0.4/§4.1, ADR-0048): it turns a
/// publication's selected layers into a <see cref="MapRenderRequest"/> over
/// the SDK render contract, composing the persisted style with
/// <see cref="MapStyle.Compose"/> and pushing a supported <c>layerDefs</c>
/// where clause down as each layer's parameterised store filter (never as
/// SQL). Tile caching is content-addressed by the service and its style. The
/// Esri request parameters that grammar reads live in
/// <see cref="MapRenderParameters"/>.
/// </summary>
internal static class MapRenderEngine
{
    /// <summary>
    /// Resolves each selected layer's keyed store and catalogue into render
    /// sources, carrying the layer's <c>layerDefs</c> filter and its export
    /// <c>time</c> extent (T-040) when the layer honours time.
    /// </summary>
    public static IReadOnlyList<MapLayerSource> Sources(
        IStoreRegistry stores, string store, IReadOnlyList<PublishedLayer> layers, IReadOnlyDictionary<int, string>? layerDefs,
        IReadOnlyDictionary<int, MapTimeExtent>? times = null)
    {
        var features = stores.Features(store);
        var catalogue = stores.Catalogue(store);
        return layers
            .Select(layer => new MapLayerSource(
                layer.Dataset, features, catalogue, layerDefs?.GetValueOrDefault(layer.Id), times?.GetValueOrDefault(layer.Id)))
            .ToArray();
    }

    /// <summary>The map layers behind the resolved serving layers.</summary>
    public static IReadOnlyList<MapLayer> ToMapLayers(IReadOnlyList<PublishedLayer> layers) =>
        [.. layers.Select(layer => new MapLayer(layer.Dataset, layer.Id, layer.Name, layer.Style))];

    /// <summary>The MapLibre style document for the selected layers.</summary>
    public static string Style(string name, IReadOnlyList<PublishedLayer> layers) =>
        MapStyle.Compose(name, ToMapLayers(layers));

    /// <summary>A content version for the tile cache: the service and its composed style.</summary>
    public static string Version(string service, string style) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(service + "\n" + style)));

    /// <summary>
    /// Reprojects an envelope between CRSs by transforming its bounding
    /// polygon, so a <c>bboxSR</c> different from the <c>imageSR</c> still
    /// frames the requested area.
    /// </summary>
    public static Envelope Project(Envelope envelope, CoordinateReference? from, CoordinateReference? to, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        if (envelope.IsEmpty || to is not { } target || from is not { } source || source == target)
        {
            return envelope;
        }

        var polygon = GeometryFactory.CreatePolygon(
            [
                new Coordinate(envelope.MinX, envelope.MinY),
                new Coordinate(envelope.MaxX, envelope.MinY),
                new Coordinate(envelope.MaxX, envelope.MaxY),
                new Coordinate(envelope.MinX, envelope.MaxY),
                new Coordinate(envelope.MinX, envelope.MinY),
            ],
            source);
        return transforms.Transform(polygon, source.ToString(), target.ToString(), cancellationToken).Envelope ?? envelope;
    }
}
