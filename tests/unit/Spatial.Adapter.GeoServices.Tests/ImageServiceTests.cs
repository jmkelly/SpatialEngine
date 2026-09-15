using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
        var root = ImageService.Root(Description(hasCatalog: false), "ESRI", new GeoServicesOptions());

        Assert.Equal(3, root.BandCount);
        Assert.Equal("U8", root.PixelType);
        Assert.Equal("esriImageServiceDataTypeRGB", root.ServiceDataType);
        Assert.Equal("ESRI", root.CopyrightText);
        Assert.Null(root.ObjectIdField);
        Assert.Null(root.Fields);
        Assert.Equal(0, root.MinPixelSize);
        Assert.Equal(0, root.MaxPixelSize);
        Assert.Equal([0.0, 0.0, 0.0], root.MinValues);
        Assert.Equal([255.0, 254.0, 255.0], root.MaxValues);
        Assert.Equal(30.386, root.PixelSizeX, 3);
        Assert.Equal(4326, root.Extent!.SpatialReference!.Wkid);
        Assert.Equal(-180, root.Extent.Xmin);
    }

    [Fact]
    public void Root_includes_the_catalog_schema_when_present()
    {
        var root = ImageService.Root(Description(hasCatalog: true), null, new GeoServicesOptions());

        Assert.Equal("OBJECTID", root.ObjectIdField);
        var fields = root.Fields!;
        Assert.Equal(["OBJECTID", "Shape", "Name"], fields.Select(field => field.Name));
        Assert.Equal("esriFieldTypeOID", fields[0].Type);
        Assert.Equal("esriFieldTypeGeometry", fields[1].Type);
        Assert.Equal("esriFieldTypeString", fields[2].Type);
    }

    [Fact]
    public void Root_derives_the_pixel_size_bounds_from_the_pyramid()
    {
        var description = Description(hasCatalog: false) with { Raster = Raster() with { MaxPyramidLevel = 3 } };

        var root = ImageService.Root(description, null, new GeoServicesOptions());

        Assert.Equal(30.386, root.MinPixelSize, 3);
        Assert.Equal(30.386 * 8, root.MaxPixelSize, 3);
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

    [Fact]
    public void Info_reports_the_pyramid_grid()
    {
        var info = ImageService.Info(Raster() with
        {
            BlockWidth = 256,
            BlockHeight = 256,
            FirstPyramidLevel = 1,
            MaxPyramidLevel = 4,
        });

        Assert.Equal(256, info.BlockWidth);
        Assert.Equal(256, info.BlockHeight);
        Assert.Equal(1, info.FirstPyramidLevel);
        Assert.Equal(4, info.MaxPyramidLevel);
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

    public static TheoryData<RasterPixelType, string> PixelTypes => new()
    {
        { RasterPixelType.U1, "U1" },
        { RasterPixelType.U2, "U2" },
        { RasterPixelType.U4, "U4" },
        { RasterPixelType.U8, "U8" },
        { RasterPixelType.S8, "S8" },
        { RasterPixelType.U16, "U16" },
        { RasterPixelType.S16, "S16" },
        { RasterPixelType.U32, "U32" },
        { RasterPixelType.S32, "S32" },
        { RasterPixelType.F32, "F32" },
        { RasterPixelType.F64, "F64" },
        { RasterPixelType.C64, "C64" },
        { RasterPixelType.C128, "C128" },
    };

    [Theory]
    [MemberData(nameof(PixelTypes))]
    public void Pixel_type_round_trips_the_supported_vocabulary(RasterPixelType type, string name)
    {
        Assert.Equal(name, ImageService.PixelType(type));
        Assert.Equal(type, ImageService.ParsePixelType(name));
        Assert.Equal(type, ImageService.ParsePixelType(name.ToLowerInvariant()));
    }

    [Fact]
    public void Unknown_pixel_type_round_trips_through_the_unknown_name()
    {
        Assert.Equal("UNKNOWN", ImageService.PixelType(RasterPixelType.Unknown));
        Assert.Equal(RasterPixelType.Unknown, ImageService.ParsePixelType("unknown"));
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

    [Fact]
    public void Download_deduplicates_a_file_shared_by_two_rasters()
    {
        var shared = new RasterFile("7~a.tif", "a.tif", "image/tiff", 10);
        var response = ImageService.Download(
        [
            (shared, 7L),
            (shared, 8L),
            (new RasterFile("9~b.png", "b.png", "image/png", 20), 9L),
        ]);

        Assert.Equal(2, response.RasterFiles.Count);
        Assert.Equal("7~a.tif", response.RasterFiles[0].Id);
        Assert.Equal(10, response.RasterFiles[0].Size);
        Assert.Equal([7L, 8L], response.RasterFiles[0].RasterIds);
        Assert.Equal([9L], response.RasterFiles[1].RasterIds);
    }

    [Fact]
    public void Thumbnail_viewport_caps_the_longest_side_and_preserves_the_ratio()
    {
        var viewport = ImageService.ThumbnailViewport(Raster() with { Width = 1000, Height = 500 }, 200);

        Assert.Equal(200, viewport.Width);
        Assert.Equal(100, viewport.Height);
        Assert.Equal("EPSG:4326", viewport.Crs);
        Assert.Equal(new Envelope(-180, -90, 180, 90), viewport.Bounds);
    }

    [Fact]
    public void Parse_raster_ids_requires_a_non_empty_list()
    {
        Assert.Equal([7L, 8L], ImageService.ParseRasterIds("7,8"));
        Assert.Throws<EsriInteropException>(() => ImageService.ParseRasterIds(null));
        Assert.Throws<EsriInteropException>(() => ImageService.ParseRasterIds(" "));
        Assert.Throws<EsriInteropException>(() => ImageService.ParseRasterIds("abc"));
    }

    [Fact]
    public void Info_includes_stored_statistics_per_band()
    {
        var info = ImageService.Info(Raster());

        Assert.Equal(3, info.Statistics!.Count);
        Assert.Equal([0.0, 255.0, 82.707, 39.838], info.Statistics[0]);
        Assert.Equal([0.0, 254.0, 107.448, 37.735], info.Statistics[1]);
    }

    [Fact]
    public void Info_omits_statistics_when_none_are_stored()
    {
        var info = ImageService.Info(Raster() with { BandStatistics = null });

        Assert.Null(info.Statistics);
    }

    [Fact]
    public void Legend_labels_one_band_entry_with_a_stable_url()
    {
        var legend = ImageLegendBuilder.Legend(Description(hasCatalog: false), [1, 2, 3], 20, 20, [0]);

        var layer = Assert.Single(legend.Layers);
        Assert.Equal(0, layer.LayerId);
        Assert.Equal("wsiearth.tif", layer.LayerName);
        Assert.Equal("Raster Layer", layer.LayerType);
        Assert.Equal("Stretched", layer.LegendType);
        var entry = Assert.Single(layer.Legend);
        Assert.Equal("Band_1", entry.Label);
        Assert.Equal("image/png", entry.ContentType);
        Assert.Equal(20, entry.Width);
        Assert.Equal(20, entry.Height);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), entry.ImageData);
        Assert.Equal(32, entry.Url.Length);
        Assert.Equal(entry.Url, ImageLegendBuilder.Legend(Description(hasCatalog: false), [9], 20, 20, [0]).Layers[0].Legend[0].Url);
    }

    [Fact]
    public void Legend_names_rgb_composites()
    {
        var legend = ImageLegendBuilder.Legend(Description(hasCatalog: false), [], 20, 20, [0, 1, 2]);

        Assert.Equal("RGB Composite", Assert.Single(legend.Layers).LegendType);
        Assert.Equal(["Band_1", "Band_2", "Band_3"], legend.Layers[0].Legend.Select(entry => entry.Label));
    }

    [Fact]
    public void Parse_legend_band_ids_defaults_to_all_bands_and_rejects_unknown_ids()
    {
        Assert.Equal([0L, 1L, 2L], ImageLegendBuilder.ParseLegendBandIds(null, 3));
        Assert.Equal([2L, 0L], ImageLegendBuilder.ParseLegendBandIds("2,0", 3));
        Assert.Throws<EsriInteropException>(() => ImageLegendBuilder.ParseLegendBandIds("3", 3));
        Assert.Throws<EsriInteropException>(() => ImageLegendBuilder.ParseLegendBandIds("-1", 3));
        Assert.Throws<EsriInteropException>(() => ImageLegendBuilder.ParseLegendBandIds("x", 3));
    }

    [Fact]
    public void Statistics_maps_stored_bands_with_full_resolution_skips()
    {
        var body = ImageService.Statistics(
        [
            new RasterBandStatistics(0, 255, 82.707, 39.838),
            new RasterBandStatistics(1, 254, 10.0, 2.0),
        ]);

        Assert.Equal(2, body.Statistics.Count);
        Assert.Equal(0, body.Statistics[0].Min);
        Assert.Equal(255, body.Statistics[0].Max);
        Assert.Equal(82.707, body.Statistics[0].Mean, 3);
        Assert.Equal(39.838, body.Statistics[0].StandardDeviation, 3);
        Assert.Equal(1, body.Statistics[0].SkipX);
        Assert.Equal(1, body.Statistics[0].SkipY);
        Assert.Equal(0, body.Statistics[0].Count);
    }

    [Fact]
    public void Histograms_passes_provider_counts_through()
    {
        var body = ImageService.Histograms([new RasterHistogram(-0.5, 255.5, [10L, 20L])]);

        var histogram = Assert.Single(body.Histograms);
        Assert.Equal(2, histogram.Size);
        Assert.Equal(-0.5, histogram.Min);
        Assert.Equal(255.5, histogram.Max);
        Assert.Equal([10L, 20L], histogram.Counts);
    }

    [Fact]
    public void Attribute_table_writes_oid_fields_and_rows()
    {
        var result = ImageService.AttributeTable(AttributeTable());

        var body = JsonDocument.Parse(ReadBody(result)).RootElement;
        Assert.Equal("OBJECTID", body.GetProperty("objectIdFieldName").GetString());
        var fields = body.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(["OID", "Value", "ClassName"], fields.Select(field => field.GetProperty("name").GetString()!));
        Assert.Equal("esriFieldTypeOID", fields[0].GetProperty("type").GetString());
        Assert.Equal("esriFieldTypeInteger", fields[1].GetProperty("type").GetString());
        Assert.Equal("esriFieldTypeString", fields[2].GetProperty("type").GetString());
        Assert.True(fields[0].GetProperty("domain").ValueKind == System.Text.Json.JsonValueKind.Null);
        var rows = body.GetProperty("features").EnumerateArray().Select(feature => feature.GetProperty("attributes")).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(0, rows[0].GetProperty("OID").GetInt64());
        Assert.Equal("Background", rows[0].GetProperty("ClassName").GetString());
        Assert.Equal("Bright", rows[1].GetProperty("ClassName").GetString());
    }

    [Fact]
    public void Attribute_table_rejects_ragged_rows_and_empty_columns()
    {
        var table = AttributeTable();
        var ragged = table with { Rows = [[AttributeValue.FromInt64(0)]] };
        Assert.Throws<EsriInteropException>(() => ImageService.AttributeTable(ragged));
        var empty = table with { Fields = [] };
        Assert.Throws<EsriInteropException>(() => ImageService.AttributeTable(empty));
    }

    private static RasterAttributeTable AttributeTable() =>
        new(
            "OBJECTID",
            [new RasterAttributeField("OID", AttributeKind.Int64, Nullable: false),
             new RasterAttributeField("Value", AttributeKind.Int64, Nullable: false),
             new RasterAttributeField("ClassName", AttributeKind.String, Length: 50)],
            [[AttributeValue.FromInt64(0), AttributeValue.FromInt64(0), AttributeValue.FromString("Background")],
             [AttributeValue.FromInt64(1), AttributeValue.FromInt64(87), AttributeValue.FromString("Bright")]]);

    private static string ReadBody(IResult result)
    {
        // The adapter writes RAT bodies through a raw JsonWriter; execute the
        // result against a default context to capture the payload.
        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        result.ExecuteAsync(context).GetAwaiter().GetResult();
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return new StreamReader(context.Response.Body).ReadToEnd();
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
