using Spatial.Core.Geometry;
using Spatial.Imagery.Vips.Raster;
using Spatial.PluginSdk;

namespace Spatial.Host.Api;

/// <summary>
/// Host configuration for the raster catalogue (<c>Spatial:Raster</c>,
/// ADR-0051). A source is a named raster file plus the georeferencing the
/// managed NetVips path cannot read from the file (CRS, extent, pixel size)
/// and optional stored band statistics. Paths stay server-side; callers only
/// ever name a configured dataset.
/// </summary>
internal sealed class RasterOptions
{
    public IReadOnlyList<RasterSource> Sources { get; set; } = [];

    /// <summary>Projects the configured sources onto the provider descriptors.</summary>
    public IReadOnlyList<RasterDatasetDescriptor> ToDescriptors()
    {
        var descriptors = new List<RasterDatasetDescriptor>();
        foreach (var source in Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Name)
                || string.IsNullOrWhiteSpace(source.Path)
                || source.Extent.Length < 4)
            {
                continue;
            }

            descriptors.Add(new RasterDatasetDescriptor(
                source.Name,
                source.Path,
                string.IsNullOrWhiteSpace(source.Crs) ? "EPSG:4326" : source.Crs,
                new Envelope(source.Extent[0], source.Extent[1], source.Extent[2], source.Extent[3]),
                source.PixelSizeX,
                source.PixelSizeY,
                source.Description,
                Statistics(source.Statistics)));
        }

        return descriptors;
    }

    private static IReadOnlyList<RasterBandStatistics>? Statistics(double[]? values) =>
        values is { Length: >= 4 }
            ? [new RasterBandStatistics(values[0], values[1], values[2], values[3])]
            : null;

    internal sealed class RasterSource
    {
        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string Crs { get; set; } = "EPSG:4326";

        /// <summary>The raster extent as <c>minX,minY,maxX,maxY</c>.</summary>
        public double[] Extent { get; set; } = [];

        public double PixelSizeX { get; set; }

        public double PixelSizeY { get; set; }

        public string? Description { get; set; }

        /// <summary>Optional stored band statistics as <c>min,max,mean,stdDev</c>.</summary>
        public double[]? Statistics { get; set; }
    }
}
