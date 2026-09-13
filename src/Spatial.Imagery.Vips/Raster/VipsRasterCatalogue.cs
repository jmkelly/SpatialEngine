using Spatial.Core.Geometry;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Raster;

/// <summary>
/// The NetVips-backed raster catalogue (ADR-0051): describes a configured
/// raster dataset, lists and identifies its catalog items, and delegates the
/// export pipeline to <see cref="VipsRasterExporter"/>. Every NetVips type and
/// every raster file path stays inside this assembly; the contract carries only
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
    private readonly VipsRasterExporter _exporter;

    public VipsRasterCatalogue(IEnumerable<RasterDatasetDescriptor> datasets, ICoordinateTransforms transforms)
    {
        ArgumentNullException.ThrowIfNull(datasets);
        _transforms = transforms ?? throw new ArgumentNullException(nameof(transforms));
        _exporter = new VipsRasterExporter(_transforms);
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
        return Task.Run(() => _exporter.Export(ExportTarget(Resolve(dataset), request.RasterId), request, cancellationToken), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RasterFile>> ListFilesAsync(
        string dataset, long? rasterId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset);
        return Task.Run(() => VipsRasterFiles.List(Resolve(dataset), rasterId), cancellationToken);
    }

    /// <inheritdoc />
    public Task<RasterFileContent> ReadFileAsync(
        string dataset, string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        return Task.Run(() => VipsRasterFiles.Read(Resolve(dataset), fileId), cancellationToken);
    }

    private RasterDatasetDescription Describe(string dataset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = Resolve(dataset);
        var hasCatalog = VipsRasterReader.HasCatalog(descriptor);
        var extent = hasCatalog ? VipsRasterReader.Union(descriptor.Items!) : descriptor.Extent;
        var info = VipsRasterReader.ReadInfo(
            descriptor.Path, extent, descriptor.Crs, descriptor.PixelSizeX, descriptor.PixelSizeY, descriptor.Statistics);
        var schema = hasCatalog ? VipsRasterReader.BuildSchema(descriptor.CatalogAttributes ?? []) : null;
        return new RasterDatasetDescription(
            dataset, descriptor.Name, descriptor.Description, info, hasCatalog, hasCatalog ? IdentityField : null, schema);
    }

    private static List<RasterCatalogItem> ListItems(RasterDatasetDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (!VipsRasterReader.HasCatalog(descriptor))
        {
            return [];
        }

        var attributes = descriptor.CatalogAttributes ?? [];
        var items = new List<RasterCatalogItem>(descriptor.Items!.Count);
        foreach (var item in descriptor.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VipsRasterReader.ValidateItem(descriptor, item, attributes);
            var info = VipsRasterReader.ReadInfo(
                item.Path,
                item.Extent,
                item.Crs ?? descriptor.Crs,
                item.PixelSizeX > 0 ? item.PixelSizeX : descriptor.PixelSizeX,
                item.PixelSizeY > 0 ? item.PixelSizeY : descriptor.PixelSizeY,
                statistics: null);
            items.Add(new RasterCatalogItem(item.ObjectId, item.Footprint, info, VipsRasterReader.Payload(item)));
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

        var overlapping = VipsRasterReader.HasCatalog(descriptor)
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
        var values = VipsRasterReader.Sample(
            samplePath, sampleExtent, samplePixelX, samplePixelY, envelope.CenterX, envelope.CenterY, cancellationToken);
        return new RasterIdentifyResult(top?.ObjectId, values, overlapping);
    }

    private RasterDatasetDescriptor Resolve(string dataset) =>
        _datasets.TryGetValue(dataset, out var descriptor)
            ? descriptor
            : throw SpatialException.Missing($"Raster dataset '{dataset}' is not configured.");

    /// <summary>
    /// The raster to export: the dataset itself, or the named catalog item
    /// when <paramref name="rasterId"/> is set. The item's own georeferencing
    /// wins, falling back to the dataset descriptor (ADR-0051).
    /// </summary>
    private static RasterDatasetDescriptor ExportTarget(RasterDatasetDescriptor descriptor, long? rasterId)
    {
        if (rasterId is not { } id)
        {
            return descriptor;
        }

        var item = descriptor.Items?.FirstOrDefault(candidate => candidate.ObjectId == id)
            ?? throw SpatialException.Missing($"Raster catalog item {id} does not exist in '{descriptor.Name}'.");
        return new RasterDatasetDescriptor(
            descriptor.Name,
            item.Path,
            item.Crs ?? descriptor.Crs,
            item.Extent,
            item.PixelSizeX > 0 ? item.PixelSizeX : descriptor.PixelSizeX,
            item.PixelSizeY > 0 ? item.PixelSizeY : descriptor.PixelSizeY);
    }

    private IGeometry ProjectGeometry(IGeometry geometry, string fromCrs, string toCrs, CancellationToken cancellationToken)
    {
        if (string.Equals(fromCrs, toCrs, StringComparison.OrdinalIgnoreCase))
        {
            return geometry;
        }

        return _transforms.Transform(geometry, RasterEnvelopes.Parse(fromCrs).ToString(), RasterEnvelopes.Parse(toCrs).ToString(), cancellationToken);
    }

    /// <summary>The catalog identity field name exposed to the adapter.</summary>
    public const string IdentityField = "OBJECTID";

    /// <summary>The catalog footprint field name exposed to the adapter.</summary>
    public const string FootprintField = "Shape";
}
