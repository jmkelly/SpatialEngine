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
        IReadOnlyList<RasterBandStatistics>? statistics)
    {
        using var image = VipsRasterFiles.Open(path);
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
