using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Shapes the Image Service resources (spec §8) from a raster provider's
/// core-typed description and items (ADR-0051). No raster value or third-party
/// type enters the adapter: the provider returns encoded images and core
/// metadata, and this type only maps them to Esri JSON. Feature-bearing
/// responses (catalog items, query, identify) are written through the shared
/// Esri codecs so footprints round-trip as core geometry.
/// </summary>
internal static class ImageService
{
    public const double CurrentVersion = 10.0;

    /// <summary>The Esri <c>pixelType</c> name for every supported core pixel type.</summary>
    private static readonly Dictionary<RasterPixelType, string> PixelTypeNames =
        new Dictionary<RasterPixelType, string>
        {
            [RasterPixelType.U1] = "U1",
            [RasterPixelType.U2] = "U2",
            [RasterPixelType.U4] = "U4",
            [RasterPixelType.U8] = "U8",
            [RasterPixelType.S8] = "S8",
            [RasterPixelType.U16] = "U16",
            [RasterPixelType.S16] = "S16",
            [RasterPixelType.U32] = "U32",
            [RasterPixelType.S32] = "S32",
            [RasterPixelType.F32] = "F32",
            [RasterPixelType.F64] = "F64",
            [RasterPixelType.C64] = "C64",
            [RasterPixelType.C128] = "C128",
        };

    /// <summary>The core pixel type named by every Esri <c>pixelType</c> value.</summary>
    private static readonly Dictionary<string, RasterPixelType> PixelTypesByName =
        new Dictionary<string, RasterPixelType>(StringComparer.Ordinal)
        {
            ["U1"] = RasterPixelType.U1,
            ["U2"] = RasterPixelType.U2,
            ["U4"] = RasterPixelType.U4,
            ["U8"] = RasterPixelType.U8,
            ["S8"] = RasterPixelType.S8,
            ["U16"] = RasterPixelType.U16,
            ["S16"] = RasterPixelType.S16,
            ["U32"] = RasterPixelType.U32,
            ["S32"] = RasterPixelType.S32,
            ["F32"] = RasterPixelType.F32,
            ["F64"] = RasterPixelType.F64,
            ["C64"] = RasterPixelType.C64,
            ["C128"] = RasterPixelType.C128,
            ["UNKNOWN"] = RasterPixelType.Unknown,
        };

    /// <summary>
    /// Builds the Image Service root (spec §8.0.3), with the truthful capability flags (T-043, ADR-0057).
    /// Raster functions and mosaicking read <c>None</c>/<c>First</c> because <c>renderingRule</c>,
    /// <c>mosaicRule</c> and multi-raster <c>rasterIds</c> are rejected and one raster's native bands
    /// are served; mensuration reads <c>None</c> for lack of sensor models; <c>hasHistograms</c>
    /// follows the <c>computeHistograms</c> provider path (every real-valued band format,
    /// complex excluded); <c>hasRasterAttributeTable</c>
    /// follows the configured table; the download caps are the enforced host options.
    /// </summary>
    public static EsriImageServerRoot Root(RasterDatasetDescription description, string? copyright, GeoServicesOptions options)
    {
        var info = description.Raster;
        var srid = MapServerResources.SridOf(info.Crs);
        var statistics = info.BandStatistics;
        return new EsriImageServerRoot(
            CurrentVersion,
            description.Description ?? description.Name,
            description.Name,
            description.Description,
            Extent(info.Extent, srid),
            info.PixelSizeX,
            info.PixelSizeY,
            info.BandCount,
            PixelType(info.PixelType),
            MinPixelSize(info),
            MaxPixelSize(info),
            copyright,
            ServiceDataType(info),
            statistics?.Select(stat => stat.Min).ToArray(),
            statistics?.Select(stat => stat.Max).ToArray(),
            statistics?.Select(stat => stat.Mean).ToArray(),
            statistics?.Select(stat => stat.StandardDeviation).ToArray(),
            description.HasCatalog ? description.ObjectIdField : null,
            description.HasCatalog && description.CatalogSchema is { } schema ? Fields(schema, description.ObjectIdField!) : null,
            AllowRasterFunction: false,
            RasterFunctionInfos: [],
            AllowedMosaicMethods: "None",
            DefaultMosaicMethod: "None",
            MosaicOperator: "First",
            MensurationCapabilities: "None",
            HasColormap: false,
            HasHistograms: info.PixelType is not (RasterPixelType.Unknown or RasterPixelType.C64 or RasterPixelType.C128),
            HasRasterAttributeTable: info.AttributeTable is not null,
            MaxDownloadImageCount: options.MaxRasterDownloadFiles,
            MaxDownloadSizeLimit: options.MaxRasterDownloadBytes,
            ServiceSourceType: "esriImageServiceSourceTypeDataset");
    }

    /// <summary>
    /// The finest (full-resolution) pixel size, or 0 when the raster has no
    /// pyramid (spec §8.0.3 reports 0.0 for a non-pyramidal service).
    /// </summary>
    private static double MinPixelSize(RasterInfo info) => info.MaxPyramidLevel > 0 ? info.PixelSizeX : 0;

    /// <summary>
    /// The coarsest overview pixel size: the full-resolution size doubled once
    /// per pyramid level, or 0 when the raster has no pyramid.
    /// </summary>
    private static double MaxPixelSize(RasterInfo info) =>
        info.MaxPyramidLevel > 0 ? info.PixelSizeX * Math.Pow(2, info.MaxPyramidLevel) : 0;

    /// <summary>Builds the Raster Info resource (spec §8.4.3), with stored per-band statistics when present.</summary>
    public static EsriRasterInfo Info(RasterInfo info)
    {
        var srid = MapServerResources.SridOf(info.Crs);
        return new EsriRasterInfo(
            new EsriPoint(info.Extent.MinX, info.Extent.MaxY),
            info.BlockWidth > 0 ? info.BlockWidth : info.Width,
            info.BlockHeight > 0 ? info.BlockHeight : 1,
            info.PixelSizeX,
            info.PixelSizeY,
            Extent(info.Extent, srid),
            info.BandCount,
            PixelType(info.PixelType),
            info.FirstPyramidLevel,
            info.MaxPyramidLevel,
            info.BandStatistics?.Select(stat => (IReadOnlyList<double>)new[] { stat.Min, stat.Max, stat.Mean, stat.StandardDeviation }).ToArray());
    }



    /// <summary>Builds the stored band statistics resource (S3 statistics/).</summary>
    public static EsriImageStatistics Statistics(IReadOnlyList<RasterBandStatistics> statistics) =>
        new([.. statistics.Select(stat => new EsriBandStatistics(
            stat.Min, stat.Max, stat.Mean, stat.StandardDeviation, SkipX: 1, SkipY: 1, Count: 0))]);

    /// <summary>Builds the computed histograms response (spec §8 computeHistograms).</summary>
    public static EsriRasterHistograms Histograms(IReadOnlyList<RasterHistogram> histograms) => new(histograms);

    /// <summary>
    /// Writes the raster attribute table (S3 raster-attribute-table/): the
    /// NLCD-shaped <c>objectIdFieldName/fields/features</c> body. The first
    /// column is the row identity (<c>esriFieldTypeOID</c>); rows must carry
    /// one value per column.
    /// </summary>
    public static IResult AttributeTable(RasterAttributeTable table) => EsriJson.Write(writer =>
    {
        if (table.Fields.Count == 0)
        {
            throw GeoServicesErrors.Invalid("The raster attribute table has no columns.");
        }

        writer.WriteStartObject();
        writer.WriteString("objectIdFieldName", table.ObjectIdField);
        WriteAttributeFields(writer, table);
        WriteAttributeRows(writer, table);
        writer.WriteEndObject();
    });

    private static void WriteAttributeFields(Utf8JsonWriter writer, RasterAttributeTable table)
    {
        writer.WritePropertyName("fields");
        writer.WriteStartArray();
        for (var i = 0; i < table.Fields.Count; i++)
        {
            var field = table.Fields[i];
            writer.WriteStartObject();
            writer.WriteString("name", field.Name);
            writer.WriteString("type", i == 0 ? EsriFieldType.Oid : EsriFieldType.FromAttributeKind(field.Kind));
            writer.WriteString("alias", field.Name);
            if (field.Length.HasValue)
            {
                writer.WriteNumber("length", field.Length.Value);
            }

            writer.WriteNull("domain");
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteAttributeRows(Utf8JsonWriter writer, RasterAttributeTable table)
    {
        writer.WritePropertyName("features");
        writer.WriteStartArray();
        foreach (var row in table.Rows)
        {
            if (row.Count != table.Fields.Count)
            {
                throw GeoServicesErrors.Invalid(
                    $"The raster attribute table row has {row.Count} values but the table declares {table.Fields.Count} columns.");
            }

            writer.WriteStartObject();
            writer.WritePropertyName("attributes");
            writer.WriteStartObject();
            for (var i = 0; i < table.Fields.Count; i++)
            {
                EsriAttributeCodec.Write(writer, table.Fields[i].Name, row[i]);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }



    /// <summary>Builds the Export Image JSON response (spec §8.0.4); the adapter owns the href.</summary>
    public static EsriImageExportResponse Export(string href, RasterViewport viewport, int srid) =>
        new(
            href,
            viewport.Width,
            viewport.Height,
            new EsriExtent(
                viewport.Bounds.MinX, viewport.Bounds.MinY, viewport.Bounds.MaxX, viewport.Bounds.MaxY,
                EsriLayerModel.SpatialReference(srid)));

    /// <summary>Builds one raster catalog item as an Esri feature (spec §8.1).</summary>
    public static IResult CatalogItem(RasterCatalogItem item, FeatureSchema schema, string objectIdField, bool returnGeometry)
    {
        var feature = Feature(item, schema);
        return EsriJson.Write(writer =>
            EsriFeatureCodec.Write(writer, feature, new EsriFeatureWriteOptions(objectIdField, item.ObjectId, ReturnGeometry: returnGeometry)));
    }

    /// <summary>Builds the Download Rasters response (spec §8.0.7), deduplicating files shared by rasters.</summary>
    public static EsriRasterDownloadResponse Download(IReadOnlyList<(RasterFile File, long RasterId)> files)
    {
        var entries = files
            .GroupBy(file => file.File.Id, StringComparer.Ordinal)
            .Select(group => new EsriRasterFileEntry(
                group.Key,
                group.First().File.Size,
                [.. group.Select(item => item.RasterId).Distinct().Order()]))
            .ToArray();
        return new EsriRasterDownloadResponse(entries);
    }

    /// <summary>
    /// The reduced viewport of a raster thumbnail (spec §8.3): the item's whole
    /// extent at a size preserving its aspect ratio, capped at
    /// <paramref name="maxSize"/> pixels on the longest side.
    /// </summary>
    public static RasterViewport ThumbnailViewport(RasterInfo info, int maxSize)
    {
        var longest = Math.Max(info.Width, info.Height);
        if (longest <= 0 || maxSize <= 0)
        {
            return new RasterViewport(info.Extent, 1, 1, info.Crs);
        }

        var scale = Math.Min(1.0, (double)maxSize / longest);
        return new RasterViewport(
            info.Extent,
            Math.Max(1, (int)Math.Round(info.Width * scale)),
            Math.Max(1, (int)Math.Round(info.Height * scale)),
            info.Crs);
    }

    /// <summary>Writes the Identify response (spec §8.0.6): pixel values, location and overlapping items.</summary>
    public static IResult Identify(
        RasterDatasetDescription description,
        RasterIdentifyResult result,
        double x,
        double y)
    {
        var srid = MapServerResources.SridOf(description.Raster.Crs);
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            if (result.ObjectId is { } objectId)
            {
                writer.WriteNumber("objectId", objectId);
            }

            writer.WriteString("value", string.Join(",", result.PixelValues.Select(value => value.ToString(CultureInfo.InvariantCulture))));
            writer.WritePropertyName("location");
            WritePoint(writer, x, y, srid);
            if (description.HasCatalog && description.CatalogSchema is { } schema && result.Items.Count > 0)
            {
                writer.WritePropertyName("catalogItems");
                writer.WriteStartObject();
                WriteFeatureSetBody(writer, description, schema, result.Items, returnGeometry: true, outFields: null);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        });
    }

    /// <summary>Maps an engine pixel type to its Esri <c>pixelType</c> string (spec §8.0.4.2).</summary>
    public static string PixelType(RasterPixelType type) =>
        PixelTypeNames.TryGetValue(type, out var name) ? name : "UNKNOWN";

    /// <summary>Parses the requested Esri <c>pixelType</c>; unsupported names are invalid arguments.</summary>
    public static RasterPixelType? ParsePixelType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return PixelTypesByName.TryGetValue(value.Trim().ToUpperInvariant(), out var type)
            ? type
            : throw GeoServicesErrors.Invalid($"Pixel type '{value}' is not supported.");
    }

    private static readonly Dictionary<string, RasterInterpolation> InterpolationByName = new(StringComparer.Ordinal)
    {
        [""] = RasterInterpolation.NearestNeighbor,
        ["RSP_NearestNeighbor"] = RasterInterpolation.NearestNeighbor,
        ["RSP_BilinearInterpolation"] = RasterInterpolation.Bilinear,
        ["RSP_CubicConvolution"] = RasterInterpolation.CubicConvolution,
        ["RSP_Majority"] = RasterInterpolation.Majority,
    };

    /// <summary>Parses the Esri <c>interpolation</c> parameter (spec §8.0.4.2).</summary>
    public static RasterInterpolation ParseInterpolation(string? value)
    {
        var key = value?.Trim() ?? string.Empty;
        return InterpolationByName.TryGetValue(key, out var parsed)
            ? parsed
            : throw GeoServicesErrors.Invalid($"Interpolation '{value}' is not supported.");
    }

    /// <summary>The raster format named by every supported Esri export <c>format</c> value (spec §8.0.4.2).</summary>
    private static readonly Dictionary<string, RasterFormat> FormatsByName =
        new Dictionary<string, RasterFormat>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = RasterFormat.Png,
            ["jpgpng"] = RasterFormat.Png,
            ["png"] = RasterFormat.Png,
            ["png8"] = RasterFormat.Png,
            ["png24"] = RasterFormat.Png,
            ["png32"] = RasterFormat.Png,
            ["jpg"] = RasterFormat.Jpeg,
            ["jpeg"] = RasterFormat.Jpeg,
            ["tif"] = RasterFormat.Tiff,
            ["tiff"] = RasterFormat.Tiff,
        };

    /// <summary>Parses the Esri export <c>format</c> parameter (spec §8.0.4.2).</summary>
    public static RasterFormat ParseFormat(string? value)
    {
        var key = value?.Trim() ?? string.Empty;
        return FormatsByName.TryGetValue(key, out var format)
            ? format
            : throw GeoServicesErrors.Invalid($"Image format '{value}' is not supported (png, jpg, tiff).");
    }

    /// <summary>Parses the optional <c>noData</c> parameter.</summary>
    public static double? ParseNoData(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var noData) || !double.IsFinite(noData))
        {
            throw GeoServicesErrors.Invalid($"'noData' must be a finite number, got '{value}'.");
        }

        return noData;
    }

    /// <summary>Parses the optional <c>compressionQuality</c> parameter (0–100).</summary>
    public static int ParseQuality(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 90;
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality) || quality is < 0 or > 100)
        {
            throw GeoServicesErrors.Invalid($"'compressionQuality' must be an integer between 0 and 100, got '{value}'.");
        }

        return quality;
    }

    /// <summary>Parses the required catalog <c>rasterIds</c> list (spec §8.0.7).</summary>
    public static IReadOnlyList<long> ParseRasterIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw GeoServicesErrors.Invalid("The 'rasterIds' parameter is required.");
        }

        var ids = EsriValueParser.ParseInt64s(value, "rasterIds");
        return ids.Count == 0 ? throw GeoServicesErrors.Invalid("The 'rasterIds' parameter is required.") : ids;
    }

    private static void WriteFeatureSetBody(
        Utf8JsonWriter writer,
        RasterDatasetDescription description,
        FeatureSchema schema,
        IReadOnlyList<RasterCatalogItem> items,
        bool returnGeometry,
        IReadOnlyList<string>? outFields)
    {
        var objectIdField = description.ObjectIdField ?? "OBJECTID";
        writer.WriteString("objectIdFieldName", objectIdField);
        writer.WriteString("geometryType", "esriGeometryPolygon");
        writer.WritePropertyName("spatialReference");
        WriteSpatialReference(writer, description.Raster.Crs);
        writer.WritePropertyName("fields");
        writer.WriteStartArray();
        WriteFieldObjects(writer, schema, objectIdField);
        writer.WriteEndArray();
        writer.WritePropertyName("features");
        writer.WriteStartArray();
        foreach (var item in items)
        {
            EsriFeatureCodec.Write(
                writer,
                Feature(item, schema),
                new EsriFeatureWriteOptions(objectIdField, item.ObjectId, outFields, returnGeometry));
        }

        writer.WriteEndArray();
        writer.WriteBoolean("exceededTransferLimit", false);
    }

    /// <summary>Builds the catalog feature from the core-typed item (footprint stays core geometry).</summary>
    public static Feature Feature(RasterCatalogItem item, FeatureSchema schema) =>
        new(
            new FeatureId(item.ObjectId.ToString(CultureInfo.InvariantCulture)),
            schema,
            item.Attributes);

    private static List<EsriField> Fields(FeatureSchema schema, string objectIdField) =>
    [
        .. schema.Fields.Select(field => new EsriField(
            field.Name,
            field.Name == objectIdField ? EsriFieldType.Oid : EsriFieldType.FromAttributeKind(field.Kind),
            field.Name,
            field.Nullable,
            false)),
    ];

    private static void WriteFieldObjects(Utf8JsonWriter writer, FeatureSchema schema, string objectIdField)
    {
        foreach (var field in schema.Fields)
        {
            writer.WriteStartObject();
            writer.WriteString("name", field.Name);
            writer.WriteString("type", field.Name == objectIdField ? EsriFieldType.Oid : EsriFieldType.FromAttributeKind(field.Kind));
            writer.WriteString("alias", field.Name);
            writer.WriteBoolean("nullable", field.Nullable);
            writer.WriteBoolean("editable", false);
            writer.WriteEndObject();
        }
    }

    private static void WriteSpatialReference(Utf8JsonWriter writer, string crs)
    {
        var srid = MapServerResources.SridOf(crs);
        if (srid > 0 && WkidMap.TryFromEpsg(srid, out var wkid))
        {
            writer.WriteStartObject();
            writer.WriteNumber("wkid", wkid);
            writer.WriteEndObject();
            return;
        }

        writer.WriteNullValue();
    }

    private static void WritePoint(Utf8JsonWriter writer, double x, double y, int srid)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", x);
        writer.WriteNumber("y", y);
        writer.WritePropertyName("spatialReference");
        WriteSpatialReference(writer, $"EPSG:{srid.ToString(CultureInfo.InvariantCulture)}");
        writer.WriteEndObject();
    }

    private static EsriExtent? Extent(Envelope extent, int srid) =>
        extent.IsEmpty ? null : new EsriExtent(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY, EsriLayerModel.SpatialReference(srid));

    private static string ServiceDataType(RasterInfo info) => info.BandCount switch
    {
        1 => "esriImageServiceDataTypeGeneric",
        3 or 4 when info.PixelType == RasterPixelType.U8 => "esriImageServiceDataTypeRGB",
        _ => "esriImageServiceDataTypeGeneric",
    };
}
