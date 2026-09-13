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
    IReadOnlyList<EsriField>? Fields);

/// <summary>The Raster Info resource (spec §8.4).</summary>
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
    int MaxPyramidLevel);

/// <summary>The Export Image JSON response (spec §8.0.4); the image itself is at <c>href</c>.</summary>
internal sealed record EsriImageExportResponse(string Href, int Width, int Height, EsriExtent Extent);

/// <summary>The Download Rasters response (spec §8.0.7): the raw files behind the selected rasters.</summary>
internal sealed record EsriRasterDownloadResponse(IReadOnlyList<EsriRasterFileEntry> RasterFiles);

/// <summary>One downloadable raw file: its opaque id, size and the raster ids it backs (spec §8.0.7.3).</summary>
internal sealed record EsriRasterFileEntry(string Id, long Size, IReadOnlyList<long> RasterIds);
