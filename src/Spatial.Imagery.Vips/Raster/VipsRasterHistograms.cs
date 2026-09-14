using NetVips;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Raster;

/// <summary>
/// Computes per-band value histograms over a raster window (spec §8
/// <c>computeHistograms</c>, ADR-0054). Every NetVips type stays inside this
/// assembly; the contract carries only the bin counts. Histograms are
/// computed from the dataset raster over the requested bounds: mosaicking
/// overlapping catalog items is an explicit non-goal (ADR-0051 I5), so the
/// caller names the dataset, never a mosaic.
/// </summary>
internal static class VipsRasterHistograms
{
    /// <summary>The bins of one 8-bit histogram, matching the Esri <c>size</c>.</summary>
    private const int Bins = 256;

    /// <summary>The bin edges of a full 8-bit range, as the Esri example reports them.</summary>
    private const double MinEdge = -0.5;

    /// <summary>The bin edges of a full 8-bit range, as the Esri example reports them.</summary>
    private const double MaxEdge = 255.5;

    public static IReadOnlyList<RasterHistogram> Compute(
        RasterDatasetDescriptor descriptor,
        RasterHistogramRequest request,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Bounds.IsEmpty)
        {
            throw SpatialException.BadArguments("The histogram bounds must not be empty.");
        }

        using var image = VipsRasterFiles.Open(descriptor.Path);
        if (image.Format != Enums.BandFormat.Uchar)
        {
            throw SpatialException.BadArguments(
                $"Raster dataset '{descriptor.Name}' has {image.Format} bands; histograms are computed for 8-bit rasters only.");
        }

        var bounds = RasterEnvelopes.Project(request.Bounds, request.Crs, descriptor.Crs, transforms, cancellationToken);
        var window = RasterEnvelopes.Window(
            descriptor.Extent, descriptor.PixelSizeX, descriptor.PixelSizeY, image.Width, image.Height, Intersect(descriptor, bounds));
        using var cropped = image.Crop(window.Left, window.Top, window.Width, window.Height);
        var histograms = new List<RasterHistogram>(cropped.Bands);
        for (var band = 0; band < cropped.Bands; band++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var extracted = cropped.ExtractBand(band);
            using var histogram = extracted.HistFind();
            histograms.Add(new RasterHistogram(MinEdge, MaxEdge, Read(histogram)));
        }

        return histograms;
    }

    /// <summary>The overlap of the requested bounds with the raster extent; disjoint bounds are invalid arguments.</summary>
    private static Envelope Intersect(RasterDatasetDescriptor descriptor, Envelope bounds)
    {
        var extent = descriptor.Extent;
        if (bounds.MinX >= extent.MaxX || bounds.MaxX <= extent.MinX
            || bounds.MinY >= extent.MaxY || bounds.MaxY <= extent.MinY)
        {
            throw SpatialException.BadArguments(
                $"The histogram bounds do not overlap raster dataset '{descriptor.Name}'.");
        }

        return new Envelope(
            Math.Max(extent.MinX, bounds.MinX),
            Math.Max(extent.MinY, bounds.MinY),
            Math.Min(extent.MaxX, bounds.MaxX),
            Math.Min(extent.MaxY, bounds.MaxY));
    }

    /// <summary>Reads the 256 bin counts of a single-band <c>HistFind</c> image.</summary>
    private static long[] Read(Image histogram)
    {
        var counts = new long[Bins];
        for (var bin = 0; bin < Bins; bin++)
        {
            counts[bin] = (long)histogram.Getpoint(bin, 0)[0];
        }

        return counts;
    }
}
