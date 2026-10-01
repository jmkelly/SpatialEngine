using System.Security.Cryptography;
using System.Text;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

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
        IStoreRegistry stores, string store, IReadOnlyList<PublishedLayer> layers, IReadOnlyDictionary<int, EsriWhere>? definitions,
        IReadOnlyDictionary<int, MapTimeExtent>? times = null)
    {
        var features = stores.Features(store);
        var catalogue = stores.Catalogue(store);
        return layers
            .Select(layer => new MapLayerSource(
                layer.Dataset, features, catalogue, definitions?.GetValueOrDefault(layer.Id)?.Predicate, times?.GetValueOrDefault(layer.Id))
            {
                // The layer's designation is publication state (ADR-0183), so
                // it rides the source rather than being read back from the
                // store: the renderer applies the same rule the request edges
                // judged the request by.
                TimeFields = layer.TimeFields,
            })
            .ToArray();
    }

    /// <summary>The map layers behind the resolved serving layers.</summary>
    public static IReadOnlyList<MapLayer> ToMapLayers(IReadOnlyList<PublishedLayer> layers) =>
        [.. layers.Select(layer => new MapLayer(layer.Dataset, layer.Id, layer.Name, layer.Style))];

    /// <summary>The MapLibre style document for the selected layers.</summary>
    public static string Style(string name, IReadOnlyList<PublishedLayer> layers) =>
        MapStyle.Compose(name, ToMapLayers(layers));

    /// <summary>
    /// A content version for the tile cache: the service, its composed style
    /// and the folded content version of every dataset the render reads
    /// (ADR-0083), so a write to one of them invalidates the tiles derived
    /// from it.
    /// </summary>
    public static string Version(string service, string style, string dataVersion) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(service + "\n" + style + "\n" + dataVersion)));

    /// <summary>
    /// The folded content version of every dataset a publication's layers read
    /// (ADR-0083), so the Esri tile key moves when their data does. Every layer
    /// is read from the service's one store, as
    /// <see cref="Sources(IStoreRegistry, string, IReadOnlyList{PublishedLayer}, IReadOnlyDictionary{int, string}?, IReadOnlyDictionary{int, MapTimeExtent}?)"/>
    /// resolves them. A store that reports no version folds in the unversioned
    /// token.
    /// </summary>
    public static Task<string> DataVersionAsync(
        IStoreRegistry stores,
        string store,
        IReadOnlyList<PublishedLayer> layers,
        CancellationToken cancellationToken) =>
        ContentVersions.FoldAsync(
            stores,
            [.. layers.Select(layer => new ContentVersionRef(store, layer.Dataset))],
            cancellationToken);

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
