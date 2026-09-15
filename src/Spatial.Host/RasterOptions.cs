using System.Globalization;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Imagery.Vips.Raster;

namespace Spatial.Host.Api;

/// <summary>
/// Host configuration for the raster catalogue (<c>Spatial:Raster</c>,
/// ADR-0051). A source is a named raster file plus the georeferencing the
/// managed NetVips path cannot read from the file (CRS, extent, pixel size)
/// and optional stored band statistics; a source with <see cref="RasterSource.Items"/>
/// is a raster catalog of per-item rasters and typed attributes. Paths stay
/// server-side; callers only ever name a configured dataset.
/// </summary>
internal sealed class RasterOptions
{
    public IReadOnlyList<RasterSource> Sources { get; set; } = [];

    /// <summary>Projects the configured sources onto the provider descriptors.</summary>
    public IReadOnlyList<RasterDatasetDescriptor> ToDescriptors()
    {
        var descriptors = new List<RasterDatasetDescriptor>();
        foreach (var source in Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Name)
                || string.IsNullOrWhiteSpace(source.Path)
                || source.Extent.Length < 4)
            {
                continue;
            }

            var attributes = Attributes(source.CatalogAttributes, source.Name);
            descriptors.Add(new RasterDatasetDescriptor(
                source.Name,
                source.Path,
                string.IsNullOrWhiteSpace(source.Crs) ? "EPSG:4326" : source.Crs,
                ToEnvelope(source.Extent),
                source.PixelSizeX,
                source.PixelSizeY,
                source.Description,
                Statistics(source.Statistics),
                CatalogAttributes: attributes,
                Items: Items(source, attributes)));
        }

        return descriptors;
    }

    private static RasterAttributeDescriptor[]? Attributes(
        IReadOnlyList<RasterAttributeSource>? attributes, string dataset)
    {
        if (attributes is not { Count: > 0 })
        {
            return null;
        }

        var descriptors = new RasterAttributeDescriptor[attributes.Count];
        for (var i = 0; i < attributes.Count; i++)
        {
            var attribute = attributes[i];
            if (string.IsNullOrWhiteSpace(attribute.Name) || attribute.Kind == AttributeKind.Geometry)
            {
                throw new InvalidOperationException(
                    $"Raster catalog '{dataset}' attribute #{i} needs a non-empty name and a non-geometry kind " +
                    "(a footprint comes from the item extent).");
            }

            descriptors[i] = new RasterAttributeDescriptor(attribute.Name, attribute.Kind, attribute.Nullable);
        }

        return descriptors;
    }

    private static RasterCatalogItemDescriptor[]? Items(
        RasterSource source, IReadOnlyList<RasterAttributeDescriptor>? attributes)
    {
        if (source.Items is not { Count: > 0 })
        {
            return null;
        }

        if (attributes is null)
        {
            throw new InvalidOperationException(
                $"Raster catalog '{source.Name}' has items but no 'CatalogAttributes'.");
        }

        var items = new RasterCatalogItemDescriptor[source.Items.Count];
        for (var i = 0; i < source.Items.Count; i++)
        {
            var item = source.Items[i];
            if (item.Extent.Length < 4)
            {
                throw new InvalidOperationException(
                    $"Raster catalog '{source.Name}' item {item.ObjectId} needs an 'Extent' of minX,minY,maxX,maxY.");
            }

            items[i] = new RasterCatalogItemDescriptor(
                item.ObjectId,
                ToFootprint(item.Extent),
                item.Path,
                ToEnvelope(item.Extent),
                ParseAttributes(source.Name, item, attributes),
                item.Crs,
                item.PixelSizeX,
                item.PixelSizeY,
                item.MetadataXml);
        }

        return items;
    }

    private static AttributeValue[] ParseAttributes(
        string dataset, RasterItemSource item, IReadOnlyList<RasterAttributeDescriptor> attributes)
    {
        if (item.Attributes.Count != attributes.Count)
        {
            throw new InvalidOperationException(
                $"Raster catalog '{dataset}' item {item.ObjectId} declares {item.Attributes.Count} values " +
                $"but {attributes.Count} columns.");
        }

        var values = new AttributeValue[attributes.Count];
        for (var i = 0; i < attributes.Count; i++)
        {
            values[i] = ParseAttribute(dataset, item.ObjectId, attributes[i], item.Attributes[i]);
        }

        return values;
    }

    private static AttributeValue ParseAttribute(
        string dataset, long objectId, RasterAttributeDescriptor attribute, string value)
    {
        if (string.IsNullOrEmpty(value) && attribute.Nullable)
        {
            return AttributeValue.Null;
        }

        try
        {
            return Parsers.TryGetValue(attribute.Kind, out var parse)
                ? parse(value)
                : AttributeValue.FromString(value);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new InvalidOperationException(
                $"Raster catalog '{dataset}' item {objectId} value '{value}' is not a valid {attribute.Kind}.", exception);
        }
    }

    private static readonly Dictionary<AttributeKind, Func<string, AttributeValue>> Parsers = new()
    {
        [AttributeKind.Boolean] = ParseBoolean,
        [AttributeKind.Int64] = ParseInt64,
        [AttributeKind.Double] = ParseDouble,
        [AttributeKind.DateTimeOffset] = ParseDateTimeOffset,
        [AttributeKind.Guid] = ParseGuid,
    };

    private static AttributeValue ParseBoolean(string value) => AttributeValue.FromBoolean(bool.Parse(value));

    private static AttributeValue ParseInt64(string value) => AttributeValue.FromInt64(long.Parse(value, CultureInfo.InvariantCulture));

    private static AttributeValue ParseDouble(string value) => AttributeValue.FromDouble(double.Parse(value, CultureInfo.InvariantCulture));

    private static AttributeValue ParseDateTimeOffset(string value) =>
        AttributeValue.FromDateTimeOffset(DateTimeOffset.Parse(value, CultureInfo.InvariantCulture));

    private static AttributeValue ParseGuid(string value) => AttributeValue.FromGuid(Guid.Parse(value));

    private static Polygon ToFootprint(double[] extent) =>
        GeometryFactory.CreatePolygon(
            [
                new Coordinate(extent[0], extent[1]),
                new Coordinate(extent[2], extent[1]),
                new Coordinate(extent[2], extent[3]),
                new Coordinate(extent[0], extent[3]),
                new Coordinate(extent[0], extent[1]),
            ]);

    private static Envelope ToEnvelope(double[] extent) => new(extent[0], extent[1], extent[2], extent[3]);

    private static IReadOnlyList<RasterBandStatistics>? Statistics(double[]? values) =>
        values is { Length: >= 4 }
            ? [new RasterBandStatistics(values[0], values[1], values[2], values[3])]
            : null;

    internal sealed class RasterSource
    {
        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public string Crs { get; set; } = "EPSG:4326";

        /// <summary>The raster extent as <c>minX,minY,maxX,maxY</c>.</summary>
        public double[] Extent { get; set; } = [];

        public double PixelSizeX { get; set; }

        public double PixelSizeY { get; set; }

        public string? Description { get; set; }

        /// <summary>Optional stored band statistics as <c>min,max,mean,stdDev</c>.</summary>
        public double[]? Statistics { get; set; }

        /// <summary>The catalog columns; a non-empty list makes this source a catalog.</summary>
        public IReadOnlyList<RasterAttributeSource> CatalogAttributes { get; set; } = [];

        /// <summary>The catalog items; each backs one raster file with values in <see cref="CatalogAttributes"/> order.</summary>
        public IReadOnlyList<RasterItemSource> Items { get; set; } = [];
    }

    /// <summary>One configured catalog column.</summary>
    internal sealed class RasterAttributeSource
    {
        public string Name { get; set; } = string.Empty;

        public AttributeKind Kind { get; set; } = AttributeKind.String;

        public bool Nullable { get; set; } = true;
    }

    /// <summary>One configured catalog item: its identity, file, extent, optional CRS/pixel size and values.</summary>
    internal sealed class RasterItemSource
    {
        public long ObjectId { get; set; }

        public string Path { get; set; } = string.Empty;

        /// <summary>The item extent as <c>minX,minY,maxX,maxY</c>.</summary>
        public double[] Extent { get; set; } = [];

        public string? Crs { get; set; }

        public double PixelSizeX { get; set; }

        public double PixelSizeY { get; set; }

        /// <summary>The values in <see cref="RasterSource.CatalogAttributes"/> order, as invariant strings.</summary>
        public IReadOnlyList<string> Attributes { get; set; } = [];

        /// <summary>
        /// The authored per-raster metadata document (ISO/FGDC XML) served by
        /// the ImageServer <c>{rasterId}/metadata</c> resource (ADR-0068).
        /// Null means the item has no authored metadata and its resource
        /// answers <c>not.found</c>.
        /// </summary>
        public string? MetadataXml { get; set; }
    }
}
