using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The offline and async surface (S4 <c>exportTiles</c> /
/// <c>estimateExportTileSize</c> map + image variants, the WMTS triple,
/// <c>generateKml</c> / the <c>kml</c> image, async <c>jobs</c>): documented
/// non-goals under the no-job architecture (ADR-0033, ADR-0060). Tiles are
/// live-rendered per scheme only, so there is no packaging model and no job
/// model to poll. Each named operation resolves its service first (an unknown
/// service stays a typed <c>not.found</c>) and is then rejected by name with a
/// typed <c>invalid.arguments</c> envelope pointing at the live alternative —
/// never silently ignored, and never a stub job model.
/// </summary>
internal static class MapOfflineRejects
{
    /// <summary>Rejects MapServer <c>exportTiles</c> (S4 export-tiles-map-service/).</summary>
    internal static Task<IResult> MapExportTiles(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, CancellationToken cancellationToken) =>
        RejectAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, "The 'exportTiles' operation is not supported: offline tile packaging (.tpk/.vtpk) needs a packaging job model, and this host serves live-rendered tiles only (ADR-0033); the root advertises exportTilesAllowed:false. Render tiles live via tile/{z}/{y}/{x}, or one image via export.", cancellationToken);

    /// <summary>Rejects MapServer <c>estimateExportTileSize</c> (S4 estimate-export-tile-size-map-service/).</summary>
    internal static Task<IResult> MapEstimateExportTileSize(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, CancellationToken cancellationToken) =>
        RejectAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, "The 'estimateExportTileSize' operation is not supported: there is no offline tile package to size — packaging needs a job model this host does not have (ADR-0033); the root advertises exportTilesAllowed:false. Render tiles live via tile/{z}/{y}/{x}.", cancellationToken);

    /// <summary>Rejects ImageServer <c>exportTiles</c> (S4 export-tiles-image-service/).</summary>
    internal static Task<IResult> ImageExportTiles(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, CancellationToken cancellationToken) =>
        RejectAsync(catalog, registry, service, "ImageServer", MapServiceKind.ImageServer, "The 'exportTiles' operation is not supported: offline raster tile packaging needs a packaging job model, and this host serves live-rendered imagery only (ADR-0033). Render one image live via exportImage.", cancellationToken);

    /// <summary>Rejects ImageServer <c>estimateExportTileSize</c> (S4 estimate-export-tile-size-image-service/).</summary>
    internal static Task<IResult> ImageEstimateExportTileSize(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, CancellationToken cancellationToken) =>
        RejectAsync(catalog, registry, service, "ImageServer", MapServiceKind.ImageServer, "The 'estimateExportTileSize' operation is not supported: there is no offline tile package to size — packaging needs a job model this host does not have (ADR-0033). Render one image live via exportImage.", cancellationToken);

    /// <summary>Rejects the WMTS surface (S4 wmts-*-map-service/: base, capabilities, tile).</summary>
    internal static Task<IResult> Wmts(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, CancellationToken cancellationToken) =>
        RejectAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, "The 'WMTS' resource is not supported: no WMTS endpoint (capabilities or tile) is served. Fetch live tiles via tile/{z}/{y}/{x} (ADR-0060 scopes WMTS for T-048).", cancellationToken);

    /// <summary>Rejects MapServer <c>generateKml</c> (S4 generate-kml/).</summary>
    internal static Task<IResult> GenerateKml(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, CancellationToken cancellationToken) =>
        RejectAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, "The 'generateKml' operation is not supported: no KML surface is served.", cancellationToken);

    /// <summary>Rejects the MapServer <c>kml</c> image (S4 kml-image-map-service/).</summary>
    internal static Task<IResult> KmlImage(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, CancellationToken cancellationToken) =>
        RejectAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, "The 'kml' image resource is not supported: no KML surface is served.", cancellationToken);

    /// <summary>Rejects the async job surface (<c>jobs</c>, one job, its results and inputs).</summary>
    internal static Task<IResult> Jobs(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, CancellationToken cancellationToken) =>
        RejectAsync(catalog, registry, service, "MapServer", MapServiceKind.MapServer, "The 'jobs' resource is not supported: long-running work runs as cancellable tasks, not observable jobs — there is no job model to poll (ADR-0033). Await the synchronous export or tile response instead.", cancellationToken);

    private static async Task<IResult> RejectAsync(
        GeoServicesCatalog catalog, IMapRegistry registry, string service,
        string serverType, MapServiceKind mapService, string message, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, serverType, mapService, cancellationToken);
            throw GeoServicesErrors.Invalid(message);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}
