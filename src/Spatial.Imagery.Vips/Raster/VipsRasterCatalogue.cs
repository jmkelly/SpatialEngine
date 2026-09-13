using NetVips;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Imagery.Vips.Imagery;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Raster;

/// <summary>
/// The NetVips-backed raster catalogue (ADR-0051): describes a configured
/// raster dataset, lists and identifies its catalog items, and exports a
/// warped, pixel-type-converted, encoded image. Every NetVips type and every
/// raster file path stays inside this assembly; the contract carries only
/// core-typed metadata, core geometry footprints and encoded image bytes.
/// Georeferencing (CRS, extent, pixel size) comes from the configured
/// <see cref="RasterDatasetDescriptor"/> because libvips does not expose the
/// GeoTIFF GeoKey tags; width/height/band count/pixel type are read from the
/// file itself.
/// </summary>
public sealed class VipsRasterCatalogue : IRasterCatalogue
{
    private readonly Dictionary<string, RasterDatasetDescriptor> _datasets;
    private readonly ICoordinateTransforms _transforms;

    public VipsRasterCatalogue(IEnumerable<RasterDatasetDescriptor> datasets, ICoordinateTransforms transforms)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        _transforms = transforms ?? throw new ArgumentNullException(nameof(transforms));
        _datasets = datasets.ToDictionary(dataset => dataset.Name, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public Task<RasterDatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset);
        return Task.Run(() => Describe(dataset, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RasterCatalogItem>> ListItemsAsync(string dataset, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset);
        return Task.Run<IReadOnlyList<RasterCatalogItem>>(() => ListItems(Resolve(dataset), cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<RasterIdentifyResult> IdentifyAsync(
        string dataset, RasterIdentifyRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset);
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Identify(Resolve(dataset), request, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<RasterImage> ExportAsync(
        string dataset, RasterExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset);
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Export(Resolve(dataset), request, cancellationToken), cancellationToken);
    }

    private RasterDatasetDescription Describe(string dataset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = Resolve(dataset);
        var hasCatalog = HasCatalog(descriptor);
        var extent = hasCatalog ? Union(descriptor.Items!) : descriptor.Extent;
        var info = ReadInfo(descriptor.Path, extent, descriptor.Crs, descriptor.PixelSizeX, descriptor.PixelSizeY, descriptor.Statistics);
        var schema = hasCatalog ? BuildSchema(descriptor.CatalogAttributes ?? []) : null;
        return new RasterDatasetDescription(
            dataset, descriptor.Name, descriptor.Description, info, hasCatalog, hasCatalog ? IdentityField : null, schema);
    }

    private static List<RasterCatalogItem> ListItems(RasterDatasetDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (!HasCatalog(descriptor))
        {
            return [];
        }

        var attributes = descriptor.CatalogAttributes ?? [];
        var items = new List<RasterCatalogItem>(descriptor.Items!.Count);
        foreach (var item in descriptor.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateItem(descriptor, item, attributes);
            var info = ReadInfo(
                item.Path,
                item.Extent,
                item.Crs ?? descriptor.Crs,
                item.PixelSizeX > 0 ? item.PixelSizeX : descriptor.PixelSizeX,
                item.PixelSizeY > 0 ? item.PixelSizeY : descriptor.PixelSizeY,
                statistics: null);
            items.Add(new RasterCatalogItem(item.ObjectId, item.Footprint, info, Payload(item)));
        }

        return items;
    }

    private RasterIdentifyResult Identify(
        RasterDatasetDescriptor descriptor, RasterIdentifyRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var geometry = ProjectGeometry(request.Geometry, request.Crs, descriptor.Crs, cancellationToken);
        if (geometry.Envelope is not { } envelope || envelope.IsEmpty)
        {
            return new RasterIdentifyResult(null, [], []);
        }

        var overlapping = HasCatalog(descriptor)
            ? ListItems(descriptor, cancellationToken)
                .Where(item => item.Footprint.Envelope is { } footprint && footprint.Intersects(envelope))
                .ToArray()
            : [];
        var top = overlapping.FirstOrDefault();
        var topDescriptor = top is null ? null : descriptor.Items!.First(item => item.ObjectId == top.ObjectId);
        var samplePath = topDescriptor?.Path ?? descriptor.Path;
        var sampleExtent = topDescriptor?.Extent ?? descriptor.Extent;
        var samplePixelX = topDescriptor is { PixelSizeX: > 0 } ? topDescriptor.PixelSizeX : descriptor.PixelSizeX;
        var samplePixelY = topDescriptor is { PixelSizeY: > 0 } ? topDescriptor.PixelSizeY : descriptor.PixelSizeY;
        var values = Sample(samplePath, sampleExtent, samplePixelX, samplePixelY, envelope.CenterX, envelope.CenterY, cancellationToken);
        return new RasterIdentifyResult(top?.ObjectId, values, overlapping);
    }

    private static double[] Sample(
        string path, Envelope extent, double pixelSizeX, double pixelSizeY, double x, double y, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var image = Open(path);
        var (px, py) = RasterEnvelopes.Pixel(extent, pixelSizeX, pixelSizeY, x, y);
        return px >= 0 && py >= 0 && px < image.Width && py < image.Height ? image.Getpoint(px, py) : [];
    }

    private RasterImage Export(RasterDatasetDescriptor descriptor, RasterExportRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

        var owned = new List<Image>();
        try
        {
            var source = Open(descriptor.Path);
            owned.Add(source);
            if (request.PixelType is { } pixelType && !RasterBandFormats.IsConvertible(source.Bands, pixelType))
            {
                throw SpatialException.BadArguments(
                    $"Pixel type '{pixelType}' cannot be produced from a {source.Bands}-band raster; supported types are " +
                    "U8 (and, for single-band rasters, U16/S16/U32/S32/F32/F64).");
            }

            var window = RasterEnvelopes.Window(
                descriptor.Extent, descriptor.PixelSizeX, descriptor.PixelSizeY, source.Width, source.Height, sourceBounds);
            var cropped = source.ExtractArea(window.Left, window.Top, window.Width, window.Height);
            owned.Add(cropped);
            var scaled = cropped.Resize(
                (double)request.Viewport.Width / window.Width,
                Kernel(request.Interpolation),
                vscale: (double)request.Viewport.Height / window.Height);
            owned.Add(scaled);
            var fitted = Fit(scaled, request.Viewport.Width, request.Viewport.Height);
            if (!ReferenceEquals(fitted, scaled))
            {
                owned.Add(fitted);
            }

            var cast = Cast(fitted, request.PixelType);
            if (!ReferenceEquals(cast, fitted))
            {
                owned.Add(cast);
            }

            var finalised = ApplyNoData(cast, request.NoData, request.Transparent, request.Format);
            if (!ReferenceEquals(finalised, cast))
            {
                owned.Add(finalised);
            }

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

    private RasterDatasetDescriptor Resolve(string dataset) =>
        _datasets.TryGetValue(dataset, out var descriptor)
            ? descriptor
            : throw SpatialException.Missing($"Raster dataset '{dataset}' is not configured.");

    private static bool HasCatalog(RasterDatasetDescriptor descriptor) =>
        descriptor.Items is { Count: > 0 };

    private static void ValidateItem(
        RasterDatasetDescriptor descriptor, RasterCatalogItemDescriptor item, IReadOnlyList<RasterAttributeDescriptor> attributes)
    {
        if (item.Attributes.Count != attributes.Count)
        {
            throw SpatialException.BadArguments(
                $"Raster catalog item {item.ObjectId} of '{descriptor.Name}' has {item.Attributes.Count} attributes " +
                $"but the catalog declares {attributes.Count} columns.");
        }
    }

    private static RasterInfo ReadInfo(
        string path,
        Envelope extent,
        string crs,
        double pixelSizeX,
        double pixelSizeY,
        IReadOnlyList<RasterBandStatistics>? statistics)
    {
        using var image = Open(path);
        return new RasterInfo(
            extent,
            crs,
            pixelSizeX,
            pixelSizeY,
            image.Width,
            image.Height,
            image.Bands,
            RasterBandFormats.ToCore(image.Format),
            statistics,
            BlockWidth: 0,
            BlockHeight: 0,
            FirstPyramidLevel: 0,
            MaxPyramidLevel: 0);
    }

    private static Image Open(string path)
    {
        if (!File.Exists(path))
        {
            throw SpatialException.Missing($"Raster file '{path}' does not exist.");
        }

        return Image.NewFromFile(path);
    }

    private static Envelope Union(IReadOnlyList<RasterCatalogItemDescriptor> items)
    {
        var extent = Envelope.Empty;
        foreach (var item in items)
        {
            extent = extent.Union(item.Extent);
        }

        return extent;
    }

    private IGeometry ProjectGeometry(IGeometry geometry, string fromCrs, string toCrs, CancellationToken cancellationToken)
    {
        if (string.Equals(fromCrs, toCrs, StringComparison.OrdinalIgnoreCase))
        {
            return geometry;
        }

        return _transforms.Transform(geometry, RasterEnvelopes.Parse(fromCrs).ToString(), RasterEnvelopes.Parse(toCrs).ToString(), cancellationToken);
    }

    private static IReadOnlyList<AttributeValue> Payload(RasterCatalogItemDescriptor item) =>
    [
        AttributeValue.FromInt64(item.ObjectId),
        AttributeValue.FromGeometry(item.Footprint),
        .. item.Attributes,
    ];

    private static FeatureSchema BuildSchema(IReadOnlyList<RasterAttributeDescriptor> attributes)
    {
        var fields = new List<FieldDefinition>
        {
            new(IdentityField, AttributeKind.Int64),
            new(FootprintField, AttributeKind.Geometry),
        };
        fields.AddRange(attributes.Select(attribute => new FieldDefinition(attribute.Name, attribute.Kind, attribute.Nullable)));
        return new FeatureSchema(fields);
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

    private static Enums.Kernel Kernel(RasterInterpolation interpolation) => interpolation switch
    {
        RasterInterpolation.NearestNeighbor => Enums.Kernel.Nearest,
        RasterInterpolation.Bilinear => Enums.Kernel.Linear,
        RasterInterpolation.CubicConvolution => Enums.Kernel.Cubic,
        RasterInterpolation.Majority => Enums.Kernel.Nearest,
        _ => throw SpatialException.BadArguments($"Unsupported raster interpolation '{interpolation}'."),
    };

    /// <summary>The catalog identity field name exposed to the adapter.</summary>
    public const string IdentityField = "OBJECTID";

    /// <summary>The catalog footprint field name exposed to the adapter.</summary>
    public const string FootprintField = "Shape";
}
