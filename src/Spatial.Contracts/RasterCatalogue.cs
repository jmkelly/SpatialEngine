using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Contracts;

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
/// One value-frequency column of a raster attribute table (the engine analogue
/// of an ArcGIS VAT): its name, core kind, nullability and, for text columns,
/// maximum length. The first field is the row identity and renders as
/// <c>esriFieldTypeOID</c> (ADR-0054).
/// </summary>
public sealed record RasterAttributeField(string Name, AttributeKind Kind, bool Nullable = true, int? Length = null);

/// <summary>
/// The categorical mapping of pixel values of one raster dataset (spec §8,
/// the <c>rasterAttributeTable</c> resource): the object-id field name, the
/// ordered columns and one core-typed row per class, each row holding
/// <see cref="Fields"/>-ordered <see cref="AttributeValue"/> entries (ADR-0054).
/// A dataset without a table carries <see langword="null"/> and its
/// resource reports a typed <c>not.found</c> instead of an empty table.
/// </summary>
public sealed record RasterAttributeTable(
    string ObjectIdField,
    IReadOnlyList<RasterAttributeField> Fields,
    IReadOnlyList<IReadOnlyList<AttributeValue>> Rows);

/// <summary>
/// A computed histogram of one raster band (spec §8, the
/// <c>computeHistograms</c> operation): <see cref="Counts"/> bin counts over
/// the half-open range [<see cref="Min"/>, <see cref="Max"/>];
/// <see cref="Size"/> is the bin count (ADR-0054).
/// </summary>
public sealed record RasterHistogram(double Min, double Max, IReadOnlyList<long> Counts)
{
    /// <summary>The number of bins in <see cref="Counts"/>.</summary>
    public int Size => Counts.Count;
}

/// <summary>
/// A histogram computation request (spec §8 <c>computeHistograms</c>): the
/// bounds to compute over, in <see cref="Crs"/>. Bounds already lie inside
/// the raster extent; anything else is <c>invalid.arguments</c> (ADR-0054).
/// </summary>
public sealed record RasterHistogramRequest(Envelope Bounds, string Crs);

/// <summary>
/// Core-typed metadata for one raster (ADR-0051): its georeferenced extent,
/// CRS identity, pixel size, pixel grid dimensions, band count and pixel type,
/// with optional stored band statistics and an optional raster attribute
/// table (ADR-0054). No raster values and no third-party type cross the
/// contract.
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
    int MaxPyramidLevel = 0,
    RasterAttributeTable? AttributeTable = null);

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
/// <see cref="RasterDatasetDescription.CatalogSchema"/> order. An item with authored
/// per-raster metadata carries it as <see cref="MetadataXml"/> (ISO/FGDC XML,
/// served byte-faithful by the ImageServer <c>{rasterId}/metadata</c> resource);
/// an item without one answers a typed <c>not.found</c> (ADR-0068).
/// </summary>
public sealed record RasterCatalogItem(
    long ObjectId,
    IGeometry Footprint,
    RasterInfo Raster,
    IReadOnlyList<AttributeValue> Attributes,
    string? MetadataXml = null);

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
    bool Transparent = true,
    long? RasterId = null);

/// <summary>
/// One raw raster file of a dataset or catalog item (spec §8.0.7/§8.5): an
/// opaque provider-owned <see cref="Id"/> (never a path), a display
/// <see cref="Name"/>, its media type and its size in bytes. The id is what a
/// client passes back to <see cref="IRasterCatalogue.ReadFileAsync"/>; the
/// provider validates it against the files it actually owns (ADR-0051).
/// </summary>
public sealed record RasterFile(string Id, string Name, string MediaType, long Size);

/// <summary>
/// The bytes of one raw raster file, bounded by the host's download cap
/// (spec §8.5). <see cref="Size"/> is the full file size even when the
/// content is a range. Raw raster bytes are file content, not a decoded
/// image, so they carry no raster metadata.
/// </summary>
public sealed record RasterFileContent(byte[] Content, string MediaType, string Name, long Size);

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

    /// <summary>
    /// Warps and encodes one dataset (or, when <see cref="RasterExportRequest.RasterId"/>
    /// names one, a single catalog item) over the requested viewport.
    /// </summary>
    Task<RasterImage> ExportAsync(
        string dataset, RasterExportRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the raw files backing a dataset or one catalog item
    /// (spec §8.0.7). <paramref name="rasterId"/> null selects the whole
    /// dataset; a named id that has no catalog is a typed
    /// <c>not.found</c>/<c>invalid.arguments</c> failure, never a silent fallback.
    /// </summary>
    Task<IReadOnlyList<RasterFile>> ListFilesAsync(
        string dataset, long? rasterId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one raw file identified by a <see cref="RasterFile.Id"/> obtained
    /// from <see cref="ListFilesAsync"/>. The id is validated against the
    /// provider's own files, so a caller can never name an arbitrary path.
    /// </summary>
    Task<RasterFileContent> ReadFileAsync(
        string dataset, string fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Computes one histogram per band over the requested bounds of a dataset
    /// (spec §8 <c>computeHistograms</c>, ADR-0054, T-054). Bounds outside the raster
    /// extent are <c>invalid.arguments</c>. 8-bit bands report full-range
    /// 256-bin histograms; other real-valued bands report 256-bin histograms
    /// over their data range (complex bands are <c>invalid.arguments</c>).
    /// </summary>
    Task<IReadOnlyList<RasterHistogram>> ComputeHistogramsAsync(
        string dataset, RasterHistogramRequest request, CancellationToken cancellationToken = default);
}
