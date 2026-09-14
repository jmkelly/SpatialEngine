using NetVips;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Raster;

/// <summary>
/// Reads the core-typed metadata of a raster dataset or catalog item (ADR-0051):
/// file dimensions and band format, catalog schema and payload, union extent
/// and centroid sampling. Extracted from <see cref="VipsRasterCatalogue"/> so
/// the catalogue keeps only orchestration.
/// </summary>
internal static class VipsRasterReader
{
    public static bool HasCatalog(RasterDatasetDescriptor descriptor) =>
        descriptor.Items is { Count: > 0 };

    public static void ValidateItem(
        RasterDatasetDescriptor descriptor, RasterCatalogItemDescriptor item, IReadOnlyList<RasterAttributeDescriptor> attributes)
    {
        if (item.Attributes.Count != attributes.Count)
        {
            throw SpatialException.BadArguments(
                $"Raster catalog item {item.ObjectId} of '{descriptor.Name}' has {item.Attributes.Count} attributes " +
                $"but the catalog declares {attributes.Count} columns.");
        }
    }

    public static RasterInfo ReadInfo(
        string path,
        Envelope extent,
        string crs,
        double pixelSizeX,
        double pixelSizeY,
        IReadOnlyList<RasterBandStatistics>? statistics,
        RasterAttributeTable? attributeTable = null)
    {
        using var image = VipsRasterFiles.Open(path);
        var levels = VipsRasterStructure.PyramidLevels(image);
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
            BlockWidth: VipsRasterStructure.TileWidth(image),
            BlockHeight: VipsRasterStructure.TileHeight(image),
            FirstPyramidLevel: levels > 0 ? 1 : 0,
            MaxPyramidLevel: levels,
            AttributeTable: attributeTable);
    }

    /// <summary>
    /// Fail-fast validation of a configured raster attribute table (ADR-0054):
    /// the table needs at least its identity column and every row must carry
    /// exactly one value per column, so a ragged table breaks startup with a
    /// typed failure instead of serving short rows.
    /// </summary>
    public static void ValidateAttributeTable(RasterDatasetDescriptor descriptor)
    {
        if (descriptor.AttributeTable is not { } table)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(table.ObjectIdField))
        {
            throw SpatialException.BadArguments(
                $"Raster dataset '{descriptor.Name}' configures a raster attribute table without an object-id field.");
        }

        if (table.Fields.Count == 0)
        {
            throw SpatialException.BadArguments(
                $"Raster dataset '{descriptor.Name}' configures a raster attribute table without columns.");
        }

        for (var row = 0; row < table.Rows.Count; row++)
        {
            if (table.Rows[row].Count != table.Fields.Count)
            {
                throw SpatialException.BadArguments(
                    $"Raster dataset '{descriptor.Name}' raster attribute table row {row} has {table.Rows[row].Count} values " +
                    $"but the table declares {table.Fields.Count} columns.");
            }
        }
    }

    public static Envelope Union(IReadOnlyList<RasterCatalogItemDescriptor> items)
    {
        var extent = Envelope.Empty;
        foreach (var item in items)
        {
            extent = extent.Union(item.Extent);
        }

        return extent;
    }

    public static IReadOnlyList<AttributeValue> Payload(RasterCatalogItemDescriptor item) =>
    [
        AttributeValue.FromInt64(item.ObjectId),
        AttributeValue.FromGeometry(item.Footprint),
        .. item.Attributes,
    ];

    public static FeatureSchema BuildSchema(IReadOnlyList<RasterAttributeDescriptor> attributes)
    {
        var fields = new List<FieldDefinition>
        {
            new(VipsRasterCatalogue.IdentityField, AttributeKind.Int64),
            new(VipsRasterCatalogue.FootprintField, AttributeKind.Geometry),
        };
        fields.AddRange(attributes.Select(attribute => new FieldDefinition(attribute.Name, attribute.Kind, attribute.Nullable)));
        return new FeatureSchema(fields);
    }

    public static double[] Sample(
        string path, Envelope extent, double pixelSizeX, double pixelSizeY, double x, double y, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var image = VipsRasterFiles.Open(path);
        var (px, py) = RasterEnvelopes.Pixel(extent, pixelSizeX, pixelSizeY, x, y);
        return px >= 0 && py >= 0 && px < image.Width && py < image.Height ? image.Getpoint(px, py) : [];
    }
}
