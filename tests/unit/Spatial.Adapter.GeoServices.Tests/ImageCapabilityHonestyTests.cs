using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// T-043: the Image Service root carries truthful capability flags (spec
/// §8.0.3 vs ground truth <c>image-root.CharlotteLAS.json</c>). Every flag
/// names behaviour proved by its own test: raster functions and mosaicking
/// are rejected on export/query, mensuration and multidimensional writes have
/// no route, histograms compute for real-valued rasters (complex excluded), the attribute-table
/// resource serves a configured table or a typed not.found, and the download
/// surface enforces the configured caps.
/// </summary>
public sealed class ImageCapabilityHonestyTests
{
    [Fact]
    public void Root_emits_honest_capability_flags()
    {
        var root = ImageService.Root(
            Description(Raster() with { AttributeTable = AttributeTable() }),
            null,
            new GeoServicesOptions { MaxRasterDownloadBytes = 12345, MaxRasterDownloadFiles = 7 });

        Assert.False(root.AllowRasterFunction);
        Assert.Empty(root.RasterFunctionInfos);
        Assert.Equal("None", root.AllowedMosaicMethods);
        Assert.Equal("None", root.DefaultMosaicMethod);
        Assert.Equal("First", root.MosaicOperator);
        Assert.Equal("None", root.MensurationCapabilities);
        Assert.False(root.HasColormap);
        Assert.True(root.HasHistograms);
        Assert.True(root.HasRasterAttributeTable);
        Assert.Equal(7, root.MaxDownloadImageCount);
        Assert.Equal(12345, root.MaxDownloadSizeLimit);
        Assert.Equal("esriImageServiceSourceTypeDataset", root.ServiceSourceType);
    }

    [Fact]
    public void Root_reports_histograms_for_float_bands()
    {
        var root = ImageService.Root(
            Description(Raster() with { PixelType = RasterPixelType.F32, BandStatistics = null }),
            null,
            new GeoServicesOptions());

        Assert.Equal("F32", root.PixelType);
        Assert.True(root.HasHistograms);
    }

    [Fact]
    public void Root_reports_no_histograms_for_complex_bands()
    {
        var root = ImageService.Root(
            Description(Raster() with { PixelType = RasterPixelType.C64, BandStatistics = null }),
            null,
            new GeoServicesOptions());

        Assert.Equal("C64", root.PixelType);
        Assert.False(root.HasHistograms);
    }

    [Fact]
    public void Root_reports_a_missing_attribute_table_honestly()
    {
        var root = ImageService.Root(Description(Raster()), null, new GeoServicesOptions());

        Assert.False(root.HasRasterAttributeTable);
        Assert.True(root.HasHistograms);
    }

    [Fact]
    public void Root_reports_the_default_download_caps()
    {
        var root = ImageService.Root(Description(Raster()), null, new GeoServicesOptions());

        Assert.Equal(1000, root.MaxDownloadImageCount);
        Assert.Equal(64L * 1024 * 1024, root.MaxDownloadSizeLimit);
    }

    private static RasterDatasetDescription Description(RasterInfo raster) =>
        new("landsat", "wsiearth.tif", "wsiearth.tif", raster, HasCatalog: false);

    private static RasterInfo Raster() =>
        new(
            new Envelope(-180, -90, 180, 90),
            "EPSG:4326",
            30.386,
            30.386,
            600,
            600,
            1,
            RasterPixelType.U8,
            [new RasterBandStatistics(0, 255, 82.707, 39.838)]);

    private static RasterAttributeTable AttributeTable() =>
        new(
            "OBJECTID",
            [new RasterAttributeField("OID", AttributeKind.Int64, Nullable: false),
             new RasterAttributeField("Value", AttributeKind.Int64, Nullable: false)],
            [[AttributeValue.FromInt64(0), AttributeValue.FromInt64(0)],
             [AttributeValue.FromInt64(1), AttributeValue.FromInt64(87)]]);
}
