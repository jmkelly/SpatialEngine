using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Raster;

/// <summary>
/// One attribute column of a configured raster catalog (ADR-0051). The
/// column order here defines the order of
/// <see cref="RasterCatalogItemDescriptor.Attributes"/> and the core
/// <see cref="FeatureSchema"/> the catalogue publishes.
/// </summary>
public sealed record RasterAttributeDescriptor(string Name, AttributeKind Kind, bool Nullable = true);

/// <summary>
/// One item of a configured raster catalog: its integer identity, core
/// geometry footprint, the raster file that backs it, its extent and pixel
/// size, and its attribute values in
/// <see cref="RasterDatasetDescriptor.CatalogAttributes"/> order. An item with
/// authored per-raster metadata carries it as <see cref="MetadataXml"/>
/// (ISO/FGDC XML, served byte-faithful by <c>{rasterId}/metadata</c>);
/// the catalogue rejects a malformed document at construction (ADR-0068).
/// </summary>
public sealed record RasterCatalogItemDescriptor(
    long ObjectId,
    IGeometry Footprint,
    string Path,
    Envelope Extent,
    IReadOnlyList<AttributeValue> Attributes,
    string? Crs = null,
    double PixelSizeX = 0,
    double PixelSizeY = 0,
    string? MetadataXml = null);

/// <summary>
/// A configured raster dataset (ADR-0051): the georeferencing the managed
/// NetVips path cannot read from the file (CRS, extent, pixel size), an
/// optional description, stored band statistics, an optional raster attribute
/// table (ADR-0054) and an optional catalog. Width/height/band count/pixel
/// type are read from <see cref="Path"/>. A dataset with <see cref="Items"/>
/// is a raster catalog; a dataset without is a single raster.
/// </summary>
public sealed record RasterDatasetDescriptor(
    string Name,
    string Path,
    string Crs,
    Envelope Extent,
    double PixelSizeX,
    double PixelSizeY,
    string? Description = null,
    IReadOnlyList<RasterBandStatistics>? Statistics = null,
    RasterAttributeTable? AttributeTable = null,
    IReadOnlyList<RasterAttributeDescriptor>? CatalogAttributes = null,
    IReadOnlyList<RasterCatalogItemDescriptor>? Items = null);
