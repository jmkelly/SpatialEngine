using System.Security.Cryptography;
using System.Text;
using Spatial.Contracts;
using Spatial.Contracts.Providers;

namespace Spatial.Spike.TileCache;

/// <summary>
/// The parts of <c>Spatial.Host</c>'s tile path that are <c>internal</c> and so
/// cannot be called from a spike under <c>eng/</c>. Each is a line-for-line
/// copy of the host's own expression, so the version this spike keys by is the
/// version the host keys by; <c>--host=URL</c> checks the copy against a live
/// host (see <see cref="HostCheck"/>) and the README says so.
///
/// The data-version fold itself is <b>not</b> mirrored: ADR-0083 puts
/// <see cref="ContentVersions"/> on <c>Spatial.Contracts</c>, so the spike calls
/// the real one. Only the composition around it is a copy.
/// </summary>
internal static class Mirror
{
    /// <summary>
    /// <c>MapRenderEngine.Version</c>: the tile key's version over the service,
    /// its composed style and the folded data version.
    /// </summary>
    public static string Version(string service, string style, string dataVersion) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(service + "\n" + style + "\n" + dataVersion)));

    /// <summary>
    /// <c>MapRenderEngine.DataVersionAsync</c>: one content-version reference
    /// per selected layer, all read from the service's one store, folded by the
    /// real <see cref="ContentVersions"/>.
    /// </summary>
    public static Task<string> DataVersionAsync(
        IStoreRegistry stores, string store, IReadOnlyList<LayerSpec> layers, CancellationToken cancellationToken) =>
        ContentVersions.FoldAsync(
            stores,
            [.. layers.Select(layer => new ContentVersionRef(store, layer.Dataset))],
            cancellationToken);

    /// <summary>
    /// <c>MapRenderEngine.Sources</c>: the resolved render source for each
    /// selected layer. A published MapServer selection carries no
    /// <c>layerDefs</c> filter or <c>time</c> extent here, so both are null —
    /// the spike's layers are unfiltered, which is the common case.
    /// </summary>
    public static IReadOnlyList<MapLayerSource> Sources(
        IStoreRegistry stores, string store, IReadOnlyList<LayerSpec> layers)
    {
        var features = stores.Features(store);
        var catalogue = stores.Catalogue(store);
        return [.. layers.Select(layer => new MapLayerSource(layer.Dataset, features, catalogue))];
    }

    /// <summary>
    /// <c>TileService.RenderAsync</c> without the <c>ITileScheme</c> validation
    /// (the spike addresses only valid tiles): look the key up, and on a miss
    /// render the tile's viewport and store it.
    /// </summary>
    public static async Task<TileResult> RenderAsync(
        IMapRenderer renderer,
        ITileCache cache,
        ITileScheme scheme,
        TileCoordinate tile,
        string version,
        MapRenderRequest request,
        CancellationToken cancellationToken)
    {
        var key = new TileCacheKey(scheme.Id, tile.Z, tile.X, tile.Y, request.Format, version);
        if (await cache.TryGetAsync(key, cancellationToken).ConfigureAwait(false) is { } cached)
        {
            return new TileResult(cached, true);
        }

        var viewport = new RasterViewport(scheme.Bounds(tile), scheme.TileSize, scheme.TileSize, scheme.Crs);
        var image = await renderer.RenderAsync(request with { Viewport = viewport }, cancellationToken).ConfigureAwait(false);
        await cache.SetAsync(key, image, cancellationToken).ConfigureAwait(false);
        return new TileResult(image, false);
    }
}

/// <summary>One rendered tile and whether it came from the cache.</summary>
internal readonly record struct TileResult(RasterImage Image, bool Cached);
