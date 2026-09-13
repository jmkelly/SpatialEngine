using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.PluginSdk;

/// <summary>
/// The storage/sample type of one raster band (ADR-0051). The names are the
/// conventional unsigned/signed/float/complex families (the same vocabulary
/// GDAL and the GeoServices spec use); the adapter maps them to Esri
/// <c>pixelType</c> strings, so no protocol name enters the SDK.
/// </summary>
public enum RasterPixelType
{
    Unknown,
    U1,
    U2,
    U4,
    U8,
    S8,
    U16,
    S16,
    U32,
    S32,
    F32,
    F64,
    C64,
    C128,
}

/// <summary>
/// The resampling kernel used when a raster export changes coordinate space
/// or pixel size (ADR-0051). The adapter maps the GeoServices
/// <c>RSP_*</c> names onto these values.
/// </summary>
public enum RasterInterpolation
{
    NearestNeighbor,
    Bilinear,
    CubicConvolution,
    Majority,
}

/// <summary>Stored per-band statistics for a raster dataset, when the provider has them (ADR-0051).</summary>
public sealed record RasterBandStatistics(double Min, double Max, double Mean, double StandardDeviation);

/// <summary>
/// Core-typed metadata for one raster (ADR-0051): its georeferenced extent,
/// CRS identity, pixel size, pixel grid dimensions, band count and pixel type,
/// with optional stored band statistics. No raster values and no third-party
/// type cross the contract.
/// </summary>
public sealed record RasterInfo(
    Envelope Extent,
    string Crs,
    double PixelSizeX,
    double PixelSizeY,
    int Width,
    int Height,
    int BandCount,
    RasterPixelType PixelType,
    IReadOnlyList<RasterBandStatistics>? BandStatistics = null,
    int BlockWidth = 0,
    int BlockHeight = 0,
    int FirstPyramidLevel = 0,
    int MaxPyramidLevel = 0);

/// <summary>
/// The description of one raster dataset (ADR-0051). <see cref="HasCatalog"/>
/// is false for a single-raster service; a catalog-bearing service also exposes
/// an integer <see cref="ObjectIdField"/> and the catalog's core
/// <see cref="CatalogSchema"/> (identity, a geometry footprint and the typed
/// attributes). The schema is <see langword="null"/> when there is no catalog.
/// </summary>
public sealed record RasterDatasetDescription(
    string Dataset,
    string Name,
    string? Description,
    RasterInfo Raster,
    bool HasCatalog,
    string? ObjectIdField = null,
    FeatureSchema? CatalogSchema = null);

/// <summary>
/// One item of a raster catalog (ADR-0051): its integer identity, core geometry
/// footprint, its own raster metadata and its attributes in
/// <see cref="RasterDatasetDescription.CatalogSchema"/> order.
/// </summary>
public sealed record RasterCatalogItem(
    long ObjectId,
    IGeometry Footprint,
    RasterInfo Raster,
    IReadOnlyList<AttributeValue> Attributes);

/// <summary>
/// A raster identify request (spec §8.0.6): the location geometry and its CRS,
/// plus an optional requested pixel size. The provider samples the mosaic at
/// the geometry's centroid and returns every catalog item that overlaps.
/// </summary>
public sealed record RasterIdentifyRequest(
    IGeometry Geometry,
    string Crs,
    double? PixelSizeX = null,
    double? PixelSizeY = null);

/// <summary>
/// A raster identify result (spec §8.0.6): the sampled pixel values (one per
/// band), the identity of the topmost catalog item when the service has a
/// catalog, and the overlapping catalog items. <see cref="PixelValues"/> is
/// empty when the location falls outside the raster.
/// </summary>
public sealed record RasterIdentifyResult(
    long? ObjectId,
    IReadOnlyList<double> PixelValues,
    IReadOnlyList<RasterCatalogItem> Items);

/// <summary>
/// A raster export request (spec §8.0.4): the output viewport (bounds already
/// in <see cref="RasterViewport.Crs"/>), resampling kernel, optional target
/// pixel type, optional nodata value for transparency, and encoding quality.
/// The provider warps the source raster into the viewport and encodes it.
/// Unsupported pixel-type conversions are <c>invalid.arguments</c>, never a
/// silent cast (ADR-0051).
/// </summary>
public sealed record RasterExportRequest(
    RasterViewport Viewport,
    RasterFormat Format = RasterFormat.Png,
    RasterInterpolation Interpolation = RasterInterpolation.NearestNeighbor,
    RasterPixelType? PixelType = null,
    double? NoData = null,
    int CompressionQuality = 90,
    bool Transparent = true);

/// <summary>
/// The raster-imagery face the ImageServer projects (ADR-0051): metadata,
/// catalog items, identify and export. Implemented by
/// <c>Spatial.Imagery.Vips</c>; paths and every NetVips type stay inside the
/// implementation, and only core-typed values and encoded image bytes cross.
/// A provider without an accessible catalog simply returns
/// <see cref="RasterDatasetDescription.HasCatalog"/> = <see langword="false"/>.
/// </summary>
public interface IRasterCatalogue
{
    /// <summary>Describes one raster dataset, or a <c>not.found</c> failure.</summary>
    Task<RasterDatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default);

    /// <summary>Lists the catalog items of a dataset; empty when the dataset has no accessible catalog.</summary>
    Task<IReadOnlyList<RasterCatalogItem>> ListItemsAsync(string dataset, CancellationToken cancellationToken = default);

    /// <summary>Identifies the pixel values at a location and the overlapping catalog items.</summary>
    Task<RasterIdentifyResult> IdentifyAsync(
        string dataset, RasterIdentifyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Warps and encodes one dataset over the requested viewport.</summary>
    Task<RasterImage> ExportAsync(
        string dataset, RasterExportRequest request, CancellationToken cancellationToken = default);
}
