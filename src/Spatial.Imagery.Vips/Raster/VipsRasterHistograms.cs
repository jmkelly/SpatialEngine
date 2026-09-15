using NetVips;
using Spatial.Contracts;
using Spatial.Core.Geometry;

namespace Spatial.Imagery.Vips.Raster;

/// <summary>
/// Computes per-band value histograms over a raster window (spec §8
/// <c>computeHistograms</c>, ADR-0054, T-054). Every NetVips type stays inside
/// this assembly; the contract carries only the bin edges and counts.
/// Histograms are computed from the dataset raster over the requested bounds:
/// mosaicking overlapping catalog items is an explicit non-goal (ADR-0051 I5),
/// so the caller names the dataset, never a mosaic.
/// </summary>
internal static class VipsRasterHistograms
{
    /// <summary>The bins of one histogram, matching the Esri <c>size</c>.</summary>
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
        if (image.Format is Enums.BandFormat.Complex or Enums.BandFormat.Dpcomplex)
        {
            throw SpatialException.BadArguments(
                $"Raster dataset '{descriptor.Name}' has {image.Format} bands; histograms are not computed for complex rasters.");
        }

        if (image.Format == Enums.BandFormat.Notset)
        {
            throw SpatialException.BadArguments(
                $"Raster dataset '{descriptor.Name}' has bands of an unknown format; histograms cannot be computed.");
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
            histograms.Add(extracted.Format == Enums.BandFormat.Uchar
                ? new RasterHistogram(MinEdge, MaxEdge, Read(extracted))
                : Scaled(extracted, cancellationToken));
        }

        return histograms;
    }

    /// <summary>
    /// The 256-bin histogram of one 8-bit band over its full range, as the Esri example reports it.
    /// </summary>
    private static long[] Read(Image band)
    {
        using var histogram = band.HistFind();
        var counts = new long[Bins];
        for (var bin = 0; bin < Bins; bin++)
        {
            counts[bin] = (long)histogram.Getpoint(bin, 0)[0];
        }

        return counts;
    }

    /// <summary>
    /// The 256-bin histogram of one non-8-bit real band (T-054): the band's
    /// data range is scaled onto the 8-bit histogram grid, so the bin edges
    /// are the data minimum and maximum. Integer bands keep the half-unit
    /// edges of the 8-bit shape ([min − 0.5, max + 0.5]); floating-point
    /// bands span [min, max] with the maximum falling in the last bin. A
    /// flat band (min equals max) has no range to scale, so every pixel
    /// falls in the middle bin of the half-unit range around the value.
    /// A fixed <c>hist_find_ndim</c> was probed and rejected: it bins
    /// floating-point values against the format range, so ordinary data
    /// collapses into the first bin instead of spreading over the data range.
    /// </summary>
    private static RasterHistogram Scaled(Image band, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var min = band.Min();
        var max = band.Max();
        if (min == max)
        {
            var flat = new long[Bins];
            flat[Bins / 2] = (long)band.Width * band.Height;
            return new RasterHistogram(min - 0.5, max + 0.5, flat);
        }

        var integer = band.Format is Enums.BandFormat.Char
            or Enums.BandFormat.Ushort
            or Enums.BandFormat.Short
            or Enums.BandFormat.Uint
            or Enums.BandFormat.Int;
        var minEdge = integer ? min - 0.5 : min;
        var maxEdge = integer ? max + 0.5 : max;
        var scale = (MaxEdge - MinEdge) / (maxEdge - minEdge);
        var offset = MinEdge - (minEdge * scale);
        using var scaled = band.Linear([scale], [offset], uchar: true);
        using var histogram = scaled.HistFind();
        var counts = new long[Bins];
        for (var bin = 0; bin < Bins; bin++)
        {
            counts[bin] = (long)histogram.Getpoint(bin, 0)[0];
        }

        return new RasterHistogram(minEdge, maxEdge, counts);
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
}
