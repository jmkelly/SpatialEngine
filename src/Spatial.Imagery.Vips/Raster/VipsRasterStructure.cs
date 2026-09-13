using NetVips;

namespace Spatial.Imagery.Vips.Raster;

/// <summary>
/// Reads the storage structure a tiled or pyramidal TIFF exposes through
/// libvips metadata (ADR-0051, plan I4): the tile size and the number of
/// internal overviews. A striped, single-resolution file exposes neither, so
/// every accessor returns 0 and the raster reads as full resolution.
/// </summary>
internal static class VipsRasterStructure
{
    /// <summary>The file's tile width in pixels, or 0 for a striped raster.</summary>
    public static int TileWidth(Image image) => Int(image, "tile-width");

    /// <summary>The file's tile height in pixels, or 0 for a striped raster.</summary>
    public static int TileHeight(Image image) => Int(image, "tile-height");

    /// <summary>The number of internal overview (sub-IFD) levels, or 0 when the raster is not pyramidal.</summary>
    public static int PyramidLevels(Image image) => Int(image, "n-subifds");

    private static int Int(Image image, string name) =>
        image.GetFields().Contains(name, StringComparer.Ordinal)
            ? Convert.ToInt32(image.Get(name), System.Globalization.CultureInfo.InvariantCulture)
            : 0;
}
