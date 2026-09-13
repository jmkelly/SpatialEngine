using NetVips;

namespace Spatial.Imagery.Vips.Raster;

/// <summary>
/// Writes a configured raster as a tiled, internally-overviewed (pyramidal)
/// TIFF — the storage layout a Cloud Optimized GeoTIFF adds — using the
/// managed NetVips path (ADR-0051, plan I4). It is an offline storage
/// operation for ingest/seed tooling; no client request reaches it. GDAL is
/// not needed: libvips' TIFF saver covers the format.
/// </summary>
internal static class VipsRasterCog
{
    /// <summary>The COG-recommended tile edge, in pixels.</summary>
    public const int TileSize = 256;

    /// <summary>Rewrites <paramref name="sourcePath"/> as a tiled pyramidal TIFF at <paramref name="destinationPath"/>.</summary>
    public static void Write(string sourcePath, string destinationPath)
    {
        using var image = VipsRasterFiles.Open(sourcePath);
        image.Tiffsave(
            destinationPath,
            compression: Enums.ForeignTiffCompression.Deflate,
            predictor: Enums.ForeignTiffPredictor.Horizontal,
            tile: true,
            tileWidth: TileSize,
            tileHeight: TileSize,
            pyramid: true,
            subifd: true,
            bigtiff: false);
    }
}
