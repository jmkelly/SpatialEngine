using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Host.Api;

namespace Spatial.Host.Tests;

/// <summary>
/// The raster host configuration (<c>Spatial:Raster</c>, ADR-0051): single
/// rasters, catalogs with typed attributes and the startup validation that
/// keeps a mis-declared catalog from reaching the provider.
/// </summary>
public sealed class RasterOptionsTests
{
    [Fact]
    public void A_single_source_projects_to_one_descriptor()
    {
        var options = new RasterOptions
        {
            Sources =
            [
                new RasterOptions.RasterSource
                {
                    Name = "wsiearth",
                    Path = "/data/wsiearth.tif",
                    Crs = "EPSG:4326",
                    Extent = [0, 0, 8, 6],
                    PixelSizeX = 1,
                    PixelSizeY = 1,
                    Statistics = [0, 255, 82.707, 39.838],
                },
            ],
        };

        var descriptor = Assert.Single(options.ToDescriptors());

        Assert.Equal("wsiearth", descriptor.Name);
        Assert.Equal(new Envelope(0, 0, 8, 6), descriptor.Extent);
        Assert.Equal(82.707, descriptor.Statistics![0].Mean, 3);
        Assert.Null(descriptor.Items);
    }

    [Fact]
    public void A_source_with_items_projects_to_a_catalog()
    {
        var options = new RasterOptions
        {
            Sources =
            [
                new RasterOptions.RasterSource
                {
                    Name = "landsat",
                    Path = "/data/landsat.tif",
                    Extent = [0, 0, 8, 6],
                    PixelSizeX = 1,
                    PixelSizeY = 1,
                    CatalogAttributes =
                    [
                        new RasterOptions.RasterAttributeSource { Name = "Name", Kind = AttributeKind.String, Nullable = false },
                        new RasterOptions.RasterAttributeSource { Name = "Cloud", Kind = AttributeKind.Double },
                    ],
                    Items =
                    [
                        new RasterOptions.RasterItemSource
                        {
                            ObjectId = 7,
                            Path = "/data/one.tif",
                            Extent = [0, 0, 8, 6],
                            Attributes = ["first", "1.5"],
                        },
                        new RasterOptions.RasterItemSource
                        {
                            ObjectId = 8,
                            Path = "/data/two.tif",
                            Extent = [4, 3, 8, 6],
                            Attributes = ["second", ""],
                        },
                    ],
                },
            ],
        };

        var descriptor = Assert.Single(options.ToDescriptors());

        Assert.Equal(["Name", "Cloud"], descriptor.CatalogAttributes!.Select(attribute => attribute.Name));
        Assert.Equal(2, descriptor.Items!.Count);
        Assert.Equal("first", descriptor.Items[0].Attributes[0].StringValue);
        Assert.Equal(1.5, descriptor.Items[0].Attributes[1].DoubleValue);
        Assert.True(descriptor.Items[1].Attributes[1].IsNull);
        Assert.Equal(new Envelope(4, 3, 8, 6), descriptor.Items[1].Footprint.Envelope);
    }

    [Fact]
    public void A_catalog_item_without_a_geometry_attribute_kind_is_rejected()
    {
        var options = CatalogOptions(attributes: [new RasterOptions.RasterAttributeSource { Name = "Shape", Kind = AttributeKind.Geometry }]);

        Assert.Throws<InvalidOperationException>(() => options.ToDescriptors());
    }

    [Fact]
    public void A_catalog_item_with_the_wrong_value_count_is_rejected()
    {
        var options = CatalogOptions();
        options.Sources[0].Items[0].Attributes = [];

        Assert.Throws<InvalidOperationException>(() => options.ToDescriptors());
    }

    [Fact]
    public void A_catalog_with_items_but_no_columns_is_rejected()
    {
        var options = CatalogOptions(attributes: []);
        options.Sources[0].CatalogAttributes = [];

        Assert.Throws<InvalidOperationException>(() => options.ToDescriptors());
    }

    [Fact]
    public void A_catalog_item_projects_its_authored_metadata()
    {
        var options = CatalogOptions();
        options.Sources[0].Items[0].MetadataXml = "<MD_Metadata><title>Item</title></MD_Metadata>";

        var descriptor = Assert.Single(options.ToDescriptors());

        Assert.Equal("<MD_Metadata><title>Item</title></MD_Metadata>", descriptor.Items![0].MetadataXml);
    }

    [Fact]
    public void A_malformed_attribute_value_is_rejected()
    {
        var options = CatalogOptions(
            attributes: [new RasterOptions.RasterAttributeSource { Name = "Cloud", Kind = AttributeKind.Double }]);
        options.Sources[0].Items[0].Attributes = ["not-a-number"];

        Assert.Throws<InvalidOperationException>(() => options.ToDescriptors());
    }

    [Fact]
    public void Every_supported_attribute_kind_round_trips()
    {
        var id = Guid.NewGuid();
        var options = CatalogOptions(
            attributes:
            [
                new RasterOptions.RasterAttributeSource { Name = "Flag", Kind = AttributeKind.Boolean },
                new RasterOptions.RasterAttributeSource { Name = "Count", Kind = AttributeKind.Int64 },
                new RasterOptions.RasterAttributeSource { Name = "Ratio", Kind = AttributeKind.Double },
                new RasterOptions.RasterAttributeSource { Name = "Seen", Kind = AttributeKind.DateTimeOffset },
                new RasterOptions.RasterAttributeSource { Name = "Id", Kind = AttributeKind.Guid },
                new RasterOptions.RasterAttributeSource { Name = "Label", Kind = AttributeKind.String },
            ]);
        options.Sources[0].Items[0].Attributes = ["true", "42", "1.5", "2020-01-02T03:04:05+00:00", id.ToString(), "plain"];

        var values = Assert.Single(options.ToDescriptors()).Items![0].Attributes;

        Assert.True(values[0].BooleanValue);
        Assert.Equal(42, values[1].Int64Value);
        Assert.Equal(1.5, values[2].DoubleValue);
        Assert.Equal(new DateTimeOffset(2020, 1, 2, 3, 4, 5, TimeSpan.Zero), values[3].DateTimeOffsetValue);
        Assert.Equal(id, values[4].GuidValue);
        Assert.Equal("plain", values[5].StringValue);
    }

    private static RasterOptions CatalogOptions(IReadOnlyList<RasterOptions.RasterAttributeSource>? attributes = null) =>
        new()
        {
            Sources =
            [
                new RasterOptions.RasterSource
                {
                    Name = "landsat",
                    Path = "/data/landsat.tif",
                    Extent = [0, 0, 8, 6],
                    PixelSizeX = 1,
                    PixelSizeY = 1,
                    CatalogAttributes = attributes ?? [new RasterOptions.RasterAttributeSource { Name = "Name", Kind = AttributeKind.String }],
                    Items =
                    [
                        new RasterOptions.RasterItemSource
                        {
                            ObjectId = 7,
                            Path = "/data/one.tif",
                            Extent = [0, 0, 8, 6],
                            Attributes = ["first"],
                        },
                    ],
                },
            ],
        };
}
