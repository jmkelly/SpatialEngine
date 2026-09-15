using NetVips;
using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Imagery.Vips.Imagery;

namespace Spatial.Imagery.Vips.Raster;

/// <summary>
/// The NetVips export pipeline (ADR-0051): projects the output viewport into
/// the raster CRS, crops, resamples, fits, converts the pixel type, applies
/// nodata transparency and encodes. Extracted from
/// <see cref="VipsRasterCatalogue"/> so the catalogue carries only metadata
/// and sampling and the image chain lives in one place.
/// </summary>
internal sealed class VipsRasterExporter
{
    private static readonly Dictionary<RasterInterpolation, Enums.Kernel> KernelByInterpolation = new()
    {
        [RasterInterpolation.NearestNeighbor] = Enums.Kernel.Nearest,
        [RasterInterpolation.Bilinear] = Enums.Kernel.Linear,
        [RasterInterpolation.CubicConvolution] = Enums.Kernel.Cubic,
        [RasterInterpolation.Majority] = Enums.Kernel.Nearest,
    };

    private readonly ICoordinateTransforms _transforms;

    public VipsRasterExporter(ICoordinateTransforms transforms) =>
        _transforms = transforms ?? throw new ArgumentNullException(nameof(transforms));

    public RasterImage Export(RasterDatasetDescriptor descriptor, RasterExportRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sourceBounds = ValidateViewport(descriptor, request, cancellationToken);
        var owned = new List<Image>();
        try
        {
            var source = VipsRasterFiles.Open(descriptor.Path);
            owned.Add(source);
            EnsurePixelType(source, request.PixelType);

            var fullWindow = RasterEnvelopes.Window(
                descriptor.Extent, descriptor.PixelSizeX, descriptor.PixelSizeY, source.Width, source.Height, sourceBounds);
            var (working, window) = Working(descriptor, source, fullWindow, request, owned);
            var cropped = working.ExtractArea(window.Left, window.Top, window.Width, window.Height);
            owned.Add(cropped);
            var scaled = cropped.Resize(
                (double)request.Viewport.Width / window.Width,
                Kernel(request.Interpolation),
                vscale: (double)request.Viewport.Height / window.Height);
            owned.Add(scaled);
            var fitted = Fit(scaled, request.Viewport.Width, request.Viewport.Height);
            AddOwned(owned, scaled, fitted);

            var cast = Cast(fitted, request.PixelType);
            AddOwned(owned, fitted, cast);

            var finalised = ApplyNoData(cast, request.NoData, request.Transparent, request.Format);
            AddOwned(owned, cast, finalised);

            return VipsEncoder.Encode(finalised, request.Format, request.CompressionQuality);
        }
        finally
        {
            foreach (var image in owned)
            {
                image.Dispose();
            }
        }
    }

    /// <summary>
    /// Picks the full-resolution image or the coarsest internal overview that
    /// still covers the requested output, and the pixel window to read from
    /// it. Falls back to full resolution when the overview is (unusually)
    /// coarser than assumed, so an export never upscales from an overview.
    /// </summary>
    private static (Image Image, (int Left, int Top, int Width, int Height) Window) Working(
        RasterDatasetDescriptor descriptor,
        Image source,
        (int Left, int Top, int Width, int Height) fullWindow,
        RasterExportRequest request,
        List<Image> owned)
    {
        var level = RasterPyramid.Select(
            VipsRasterStructure.PyramidLevels(source),
            fullWindow.Width,
            fullWindow.Height,
            request.Viewport.Width,
            request.Viewport.Height);
        if (level == 0)
        {
            return (source, fullWindow);
        }

        var overview = VipsRasterFiles.OpenOverview(descriptor.Path, level);
        owned.Add(overview);
        var window = RasterPyramid.ScaleWindow(fullWindow, source.Width, source.Height, overview.Width, overview.Height);
        return window.Width >= request.Viewport.Width && window.Height >= request.Viewport.Height
            ? (overview, window)
            : (source, fullWindow);
    }

    private Envelope ValidateViewport(
        RasterDatasetDescriptor descriptor, RasterExportRequest request, CancellationToken cancellationToken)
    {
        if (!request.Viewport.IsValid)
        {
            throw SpatialException.BadArguments("The viewport requires a non-empty bounds and a positive pixel size.");
        }

        var sourceBounds = RasterEnvelopes.Project(
            request.Viewport.Bounds, request.Viewport.Crs, descriptor.Crs, _transforms, cancellationToken);
        if (sourceBounds.IsEmpty || !sourceBounds.Intersects(descriptor.Extent))
        {
            throw SpatialException.BadArguments("The requested bbox does not overlap the raster extent.");
        }

        return sourceBounds;
    }

    private static void EnsurePixelType(Image source, RasterPixelType? pixelType)
    {
        if (pixelType is { } wanted && !RasterBandFormats.IsConvertible(source.Bands, wanted))
        {
            throw SpatialException.BadArguments(
                $"Pixel type '{wanted}' cannot be produced from a {source.Bands}-band raster; supported types are " +
                "U8 (and, for single-band rasters, U16/S16/U32/S32/F32/F64).");
        }
    }

    private static void AddOwned(List<Image> owned, Image previous, Image current)
    {
        if (!ReferenceEquals(previous, current))
        {
            owned.Add(current);
        }
    }

    private static Image Fit(Image image, int width, int height) =>
        image.Width == width && image.Height == height
            ? image
            : image.Embed(0, 0, width, height, Enums.Extend.Copy);

    private static Image Cast(Image image, RasterPixelType? target)
    {
        if (target is not { } wanted || RasterBandFormats.ToCore(image.Format) == wanted)
        {
            return image;
        }

        return image.Cast(RasterBandFormats.ToVips(wanted));
    }

    private static Image ApplyNoData(Image image, double? noData, bool transparent, RasterFormat format)
    {
        if (noData is not { } value || !transparent || format == RasterFormat.Jpeg || image.Bands is not (1 or 3))
        {
            return image;
        }

        using var band = image[0];
        using var condition = band.NotEqual(value);
        using var alpha = condition.Ifthenelse(255, 0);
        using var joined = image.Bandjoin(alpha);
        return joined.Copy(interpretation: image.Bands == 3 ? Enums.Interpretation.Srgb : image.Interpretation);
    }

    private static Enums.Kernel Kernel(RasterInterpolation interpolation) =>
        KernelByInterpolation.TryGetValue(interpolation, out var kernel)
            ? kernel
            : throw SpatialException.BadArguments($"Unsupported raster interpolation '{interpolation}'.");
}
