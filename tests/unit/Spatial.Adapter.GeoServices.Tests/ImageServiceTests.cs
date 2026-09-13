using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The Image Service shaping (spec §8, ADR-0051): root metadata, raster info,
/// pixel-type/interpolation/format parsing and catalog feature mapping. The
/// adapter maps core-typed provider metadata only; no raster value crosses.
/// </summary>
public sealed class ImageServiceTests
{
    [Fact]
    public void Root_omits_fields_without_a_catalog()
    {
        var root = ImageService.Root(Description(hasCatalog: false), "ESRI");

        Assert.Equal(3, root.BandCount);
        Assert.Equal("U8", root.PixelType);
        Assert.Equal("esriImageServiceDataTypeRGB", root.ServiceDataType);
        Assert.Equal("ESRI", root.CopyrightText);
        Assert.Null(root.ObjectIdField);
        Assert.Null(root.Fields);
        Assert.Equal([0.0, 0.0, 0.0], root.MinValues);
        Assert.Equal([255.0, 254.0, 255.0], root.MaxValues);
        Assert.Equal(30.386, root.PixelSizeX, 3);
        Assert.Equal(4326, root.Extent!.SpatialReference!.Wkid);
        Assert.Equal(-180, root.Extent.Xmin);
    }

    [Fact]
    public void Root_includes_the_catalog_schema_when_present()
    {
        var root = ImageService.Root(Description(hasCatalog: true), null);

        Assert.Equal("OBJECTID", root.ObjectIdField);
        var fields = root.Fields!;
        Assert.Equal(["OBJECTID", "Shape", "Name"], fields.Select(field => field.Name));
        Assert.Equal("esriFieldTypeOID", fields[0].Type);
        Assert.Equal("esriFieldTypeGeometry", fields[1].Type);
        Assert.Equal("esriFieldTypeString", fields[2].Type);
    }

    [Fact]
    public void Info_reports_origin_and_pixel_grid()
    {
        var info = ImageService.Info(Raster());

        Assert.Equal(-180, info.Origin.X);
        Assert.Equal(90, info.Origin.Y);
        Assert.Equal(600, info.BlockWidth);
        Assert.Equal(1, info.BlockHeight);
        Assert.Equal("U8", info.PixelType);
        Assert.Equal(4326, info.Extent!.SpatialReference!.Wkid);
    }

    [Theory]
    [InlineData("U8", RasterPixelType.U8)]
    [InlineData("f32", RasterPixelType.F32)]
    [InlineData("C128", RasterPixelType.C128)]
    [InlineData(null, null)]
    public void Pixel_type_parses_known_values(string? value, RasterPixelType? expected)
    {
        Assert.Equal(expected, ImageService.ParsePixelType(value));
    }

    [Fact]
    public void Pixel_type_rejects_unknown_values()
    {
        var failure = Assert.Throws<EsriInteropException>(() => ImageService.ParsePixelType("S64"));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Theory]
    [InlineData("RSP_NearestNeighbor", RasterInterpolation.NearestNeighbor)]
    [InlineData("RSP_BilinearInterpolation", RasterInterpolation.Bilinear)]
    [InlineData("RSP_CubicConvolution", RasterInterpolation.CubicConvolution)]
    [InlineData("RSP_Majority", RasterInterpolation.Majority)]
    [InlineData(null, RasterInterpolation.NearestNeighbor)]
    public void Interpolation_parses_known_values(string? value, RasterInterpolation expected)
    {
        Assert.Equal(expected, ImageService.ParseInterpolation(value));
    }

    [Fact]
    public void Interpolation_rejects_unknown_values()
    {
        Assert.Throws<EsriInteropException>(() => ImageService.ParseInterpolation("RSP_Lanczos"));
    }

    [Theory]
    [InlineData("png", RasterFormat.Png)]
    [InlineData("png24", RasterFormat.Png)]
    [InlineData("jpg", RasterFormat.Jpeg)]
    [InlineData("tiff", RasterFormat.Tiff)]
    [InlineData(null, RasterFormat.Png)]
    public void Format_parses_known_values(string? value, RasterFormat expected)
    {
        Assert.Equal(expected, ImageService.ParseFormat(value));
    }

    [Fact]
    public void Format_rejects_unsupported_containers()
    {
        Assert.Throws<EsriInteropException>(() => ImageService.ParseFormat("bmp"));
        Assert.Throws<EsriInteropException>(() => ImageService.ParseFormat("gif"));
    }

    [Fact]
    public void No_data_and_quality_validate_their_ranges()
    {
        Assert.Null(ImageService.ParseNoData(null));
        Assert.Equal(0, ImageService.ParseNoData("0"));
        Assert.Equal(90, ImageService.ParseQuality(null));
        Assert.Equal(75, ImageService.ParseQuality("75"));
        Assert.Throws<EsriInteropException>(() => ImageService.ParseNoData("nan"));
        Assert.Throws<EsriInteropException>(() => ImageService.ParseQuality("101"));
    }

    [Fact]
    public void Feature_maps_the_catalog_item_identity_and_footprint()
    {
        var description = Description(hasCatalog: true);
        var item = Item();

        var feature = ImageService.Feature(item, description.CatalogSchema!);

        Assert.Equal("37", feature.Id.Value);
        Assert.IsType<Polygon>(feature["Shape"].GeometryValue);
        Assert.Equal("first", feature["Name"].StringValue);
        Assert.Equal(37, feature["OBJECTID"].Int64Value);
    }

    private static RasterCatalogItem Item()
    {
        var polygon = GeometryFactory.CreatePolygon(
            [
                new Coordinate(0, 0),
                new Coordinate(1, 0),
                new Coordinate(1, 1),
                new Coordinate(0, 1),
                new Coordinate(0, 0),
            ],
            CoordinateReference.Epsg(4326));
        return new RasterCatalogItem(
            37,
            polygon,
            Raster(),
            [AttributeValue.FromInt64(37), AttributeValue.FromGeometry(polygon), AttributeValue.FromString("first")]);
    }

    private static RasterDatasetDescription Description(bool hasCatalog) =>
        new(
            "landsat",
            "wsiearth.tif",
            "wsiearth.tif",
            Raster(),
            hasCatalog,
            hasCatalog ? "OBJECTID" : null,
            hasCatalog ? CatalogSchema() : null);

    private static FeatureSchema CatalogSchema() =>
        new(
        [
            new FieldDefinition("OBJECTID", AttributeKind.Int64),
            new FieldDefinition("Shape", AttributeKind.Geometry),
            new FieldDefinition("Name", AttributeKind.String),
        ]);

    private static RasterInfo Raster() =>
        new(
            new Envelope(-180, -90, 180, 90),
            "EPSG:4326",
            30.386,
            30.386,
            600,
            600,
            3,
            RasterPixelType.U8,
            [
                new RasterBandStatistics(0, 255, 82.707, 39.838),
                new RasterBandStatistics(0, 254, 107.448, 37.735),
                new RasterBandStatistics(0, 255, 60.118, 36.466),
            ]);
}
