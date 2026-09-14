using Spatial.PluginSdk;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Image Service wire shapes (spec §8). Written explicitly so the adapter
/// owns its JSON independently of the Feature/Map Server records; nullable
/// members are omitted by the facade's serializer.
/// </summary>
internal sealed record EsriImageServerRoot(
    double CurrentVersion,
    string ServiceDescription,
    string Name,
    string? Description,
    EsriExtent? Extent,
    double PixelSizeX,
    double PixelSizeY,
    int BandCount,
    string PixelType,
    double MinPixelSize,
    double MaxPixelSize,
    string? CopyrightText,
    string ServiceDataType,
    IReadOnlyList<double>? MinValues,
    IReadOnlyList<double>? MaxValues,
    IReadOnlyList<double>? MeanValues,
    IReadOnlyList<double>? StdvValues,
    string? ObjectIdField,
    IReadOnlyList<EsriField>? Fields,
    bool AllowRasterFunction,
    IReadOnlyList<EsriRasterFunctionInfo> RasterFunctionInfos,
    string AllowedMosaicMethods,
    string DefaultMosaicMethod,
    string MosaicOperator,
    string MensurationCapabilities,
    bool HasColormap,
    bool HasHistograms,
    bool HasRasterAttributeTable,
    int MaxDownloadImageCount,
    long MaxDownloadSizeLimit,
    string ServiceSourceType);

/// <summary>One raster function template of the service (spec §8 <c>rasterFunctionInfos</c>).</summary>
internal sealed record EsriRasterFunctionInfo(string Name, string? Description, string? Help);

/// <summary>The Raster Info resource (spec §8.4): grid, pyramid and the stored per-band statistics.</summary>
internal sealed record EsriRasterInfo(
    EsriPoint Origin,
    int BlockWidth,
    int BlockHeight,
    double PixelSizeX,
    double PixelSizeY,
    EsriExtent? Extent,
    int BandCount,
    string PixelType,
    int FirstPyramidLevel,
    int MaxPyramidLevel,
    IReadOnlyList<IReadOnlyList<double>>? Statistics);

/// <summary>The Export Image JSON response (spec §8.0.4); the image itself is at <c>href</c>.</summary>
internal sealed record EsriImageExportResponse(string Href, int Width, int Height, EsriExtent Extent);

/// <summary>The Legend resource (spec §8, S3 legend-image-service/): one entry per band.</summary>
internal sealed record EsriImageLegend(IReadOnlyList<EsriImageLegendLayer> Layers);

/// <summary>One legend layer: the dataset rendered at 20x20 pixels per band entry.</summary>
internal sealed record EsriImageLegendLayer(
    int LayerId,
    string LayerName,
    string LayerType,
    int MinScale,
    int MaxScale,
    string LegendType,
    IReadOnlyList<EsriImageLegendEntry> Legend);

/// <summary>One band swatch: the 20x20 dataset render, base64-encoded like the Esri reference.</summary>
internal sealed record EsriImageLegendEntry(
    string Label,
    string Url,
    string ImageData,
    string ContentType,
    int Height,
    int Width);

/// <summary>The stored band statistics resource (spec §8, S3 statistics/).</summary>
internal sealed record EsriImageStatistics(IReadOnlyList<EsriBandStatistics> Statistics);

/// <summary>One band's stored statistics; skip factors are 1 (full resolution) like the reference.</summary>
internal sealed record EsriBandStatistics(
    double Min,
    double Max,
    double Mean,
    double StandardDeviation,
    int SkipX,
    int SkipY,
    int Count);

/// <summary>The computed histograms response (spec §8 computeHistograms): one histogram per band.</summary>
internal sealed record EsriRasterHistograms(IReadOnlyList<RasterHistogram> Histograms);

/// <summary>The service metadata record: the described dataset as JSON (no authored metadata store).</summary>
internal sealed record EsriImageMetadata(
    string Name,
    string? Description,
    EsriExtent? Extent,
    EsriSpatialReferenceDto? SpatialReference,
    int BandCount,
    string PixelType,
    string ServiceDataType,
    string? CopyrightText,
    IReadOnlyList<double>? MinValues,
    IReadOnlyList<double>? MaxValues,
    IReadOnlyList<double>? MeanValues,
    IReadOnlyList<double>? StdvValues);

/// <summary>The Download Rasters response (spec §8.0.7): the raw files behind the selected rasters.</summary>
internal sealed record EsriRasterDownloadResponse(IReadOnlyList<EsriRasterFileEntry> RasterFiles);

/// <summary>One downloadable raw file: its opaque id, size and the raster ids it backs (spec §8.0.7.3).</summary>
internal sealed record EsriRasterFileEntry(string Id, long Size, IReadOnlyList<long> RasterIds);
