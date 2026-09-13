using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NetVips;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Imagery.Vips.Raster;
using Spatial.PluginSdk;

namespace Spatial.Host.Tests;

/// <summary>
/// The GeoServices ImageServer projection (spec §8, ADR-0051) over a
/// <c>PublicationKind.Image</c> publication: root metadata, raster info,
/// catalog listing/item, identify and <c>exportImage</c> (bytes and JSON
/// href), plus absent-catalog and pixel-type rejection.
/// </summary>
public sealed class GeoServicesImageTests : IDisposable
{
    private static readonly string[] ImageServices = ["image"];

    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";
    private const string Name = "wsiearth";
    private const string Dataset = "raster.wsiearth";
    private const string ColorName = "color";
    private const string ColorDataset = "raster.color";
    private const string CatalogName = "landsat";
    private const string CatalogDatasetName = "raster.landsat";
    private const int Width = 8;
    private const int Height = 6;

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-image-").FullName;
    private readonly string _rasterPath;
    private readonly string _colorPath;
    private readonly WebApplicationFactory<Program> _factory;

    public GeoServicesImageTests()
    {
        _rasterPath = System.IO.Path.Combine(_directory, "wsiearth.tif");
        _colorPath = System.IO.Path.Combine(_directory, "color.tif");
        WriteRaster(_rasterPath, bands: 1, noDataPixel: false);
        WriteRaster(_colorPath, bands: 3, noDataPixel: true);
        _factory = new ImageFactory(_directory, _rasterPath, Dataset, catalog: false);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static void WriteRaster(string path, int bands, bool noDataPixel)
    {
        var pixels = new byte[Width * Height * bands];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var value = (byte)((y * 16) + x);
                if (noDataPixel && x == 1 && y == 1)
                {
                    value = 0;
                }

                for (var band = 0; band < bands; band++)
                {
                    pixels[((y * Width) + x) * bands + band] = value;
                }
            }
        }

        using var image = Image.NewFromMemory(pixels, Width, Height, bands, Enums.BandFormat.Uchar);
        image.WriteToFile(path);
    }

    private static void WriteTiledPyramid(string path, int width, int height, int tileSize)
    {
        var pixels = new byte[width * height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % 256);
        }

        using var image = Image.NewFromMemory(pixels, width, height, 1, Enums.BandFormat.Uchar);
        image.Tiffsave(
            path,
            compression: Enums.ForeignTiffCompression.Deflate,
            predictor: Enums.ForeignTiffPredictor.Horizontal,
            tile: true,
            tileWidth: tileSize,
            tileHeight: tileSize,
            pyramid: true,
            subifd: true);
    }

    private static Task<HttpClient> ImageServiceAsync(
        WebApplicationFactory<Program> factory, string service, string dataset) =>
        PutMapAsync(factory, service, dataset, ImageServices);

    private static async Task<HttpClient> PutMapAsync(
        WebApplicationFactory<Program> factory, string service, string dataset, string[] services)
    {
        var client = factory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name = service,
            store = "raster",
            services,
            layers = new[] { new { dataset, layerId = 0, name = service, kind = "image" } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/maps/{service}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    [Fact]
    public async Task A_map_that_enables_the_image_service_serves_an_image_server()
    {
        var client = await ImageServiceAsync(_factory, Name, Dataset);

        var services = (await BodyAsync(await client.GetAsync($"{Root}?f=json"))).GetProperty("services").EnumerateArray()
            .Select(service => (service.GetProperty("name").GetString(), service.GetProperty("type").GetString()))
            .ToArray();
        var root = await BodyAsync(await client.GetAsync($"{Root}/{Name}/ImageServer?f=json"));

        Assert.Contains((Name, "ImageServer"), services);
        Assert.Equal(4326, root.GetProperty("extent").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public async Task A_map_that_does_not_enable_the_image_service_has_no_image_server()
    {
        var client = await PutMapAsync(_factory, "draft", Dataset, []);

        var response = await client.GetAsync($"{Root}/draft/ImageServer?f=json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task The_catalog_advertises_the_image_service()
    {
        var client = await ImageServiceAsync(_factory, Name, Dataset);

        var services = (await BodyAsync(await client.GetAsync($"{Root}?f=json"))).GetProperty("services").EnumerateArray()
            .Select(service => (service.GetProperty("name").GetString(), service.GetProperty("type").GetString()))
            .ToArray();

        Assert.Contains((Name, "ImageServer"), services);
    }

    [Fact]
    public async Task The_root_returns_the_spec_metadata()
    {
        var client = await ImageServiceAsync(_factory, Name, Dataset);

        var root = await BodyAsync(await client.GetAsync($"{Root}/{Name}/ImageServer?f=json"));

        Assert.Equal(1, root.GetProperty("pixelSizeX").GetDouble());
        Assert.Equal(1, root.GetProperty("bandCount").GetInt32());
        Assert.Equal("U8", root.GetProperty("pixelType").GetString());
        Assert.Equal("esriImageServiceDataTypeGeneric", root.GetProperty("serviceDataType").GetString());
        Assert.Equal(4326, root.GetProperty("extent").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.Equal(0, root.GetProperty("extent").GetProperty("xmin").GetDouble());
        Assert.False(root.TryGetProperty("objectIdField", out _));
        Assert.False(root.TryGetProperty("fields", out _));
        Assert.Equal(82.707, root.GetProperty("meanValues")[0].GetDouble(), 3);
    }

    [Fact]
    public async Task The_root_reports_pyramid_pixel_size_bounds()
    {
        var tiledPath = System.IO.Path.Combine(_directory, "tiled.tif");
        WriteTiledPyramid(tiledPath, width: 64, height: 48, tileSize: 16);
        await using var tiledFactory = new ImageFactory(_directory, tiledPath, Dataset, catalog: false, width: 64, height: 48);
        var client = await ImageServiceAsync(tiledFactory, Name, Dataset);

        var root = await BodyAsync(await client.GetAsync($"{Root}/{Name}/ImageServer?f=json"));

        Assert.Equal(1, root.GetProperty("minPixelSize").GetDouble());
        Assert.Equal(4, root.GetProperty("maxPixelSize").GetDouble());
    }

    [Fact]
    public async Task Export_streams_a_golden_image_for_f_image()
    {
        var client = await ImageServiceAsync(_factory, Name, Dataset);

        var response = await client.GetAsync(
            $"{Root}/{Name}/ImageServer/exportImage?f=image&bbox=0,0,{Width},{Height}&size={Width},{Height}&format=png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Width.ToString(CultureInfo.InvariantCulture), response.Headers.GetValues("X-Raster-Width").Single());
        using var image = Image.NewFromBuffer(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, image.Getpoint(0, 0)[0]);
        Assert.Equal(87, image.Getpoint(7, 5)[0]);
    }

    [Fact]
    public async Task Export_json_returns_an_image_href()
    {
        var client = await ImageServiceAsync(_factory, Name, Dataset);

        var export = await BodyAsync(await client.GetAsync(
            $"{Root}/{Name}/ImageServer/exportImage?f=json&bbox=0,0,{Width},{Height}&size=4,3&format=png"));

        Assert.Equal(4, export.GetProperty("width").GetInt32());
        Assert.Equal(3, export.GetProperty("height").GetInt32());
        Assert.Contains("f=image", export.GetProperty("href").GetString());
        Assert.Equal(4326, export.GetProperty("extent").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public async Task Export_reprojects_the_bbox_when_the_image_sr_differs()
    {
        var client = await ImageServiceAsync(_factory, Name, Dataset);

        var export = await BodyAsync(await client.GetAsync(
            $"{Root}/{Name}/ImageServer/exportImage?f=json&bbox=0,0,{Width},{Height}&bboxSR=4326&imageSR=3857&size=4,3"));

        var extent = export.GetProperty("extent");
        Assert.Equal(3857, extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32());
        Assert.True(extent.GetProperty("xmax").GetDouble() > 800_000);
    }

    [Fact]
    public async Task Export_rejects_an_unsupported_pixel_type()
    {
        await using var colorFactory = new ImageFactory(_directory, _colorPath, ColorDataset, catalog: false);
        var client = await ImageServiceAsync(colorFactory, ColorName, ColorDataset);

        var response = await client.GetAsync(
            $"{Root}/{ColorName}/ImageServer/exportImage?f=json&bbox=0,0,{Width},{Height}&size=4,3&pixelType=F32");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// T-015 closeout: raster-selection parameters a real client sends
    /// (mosaicRule, renderingRule, bandIds) change which pixels combine.
    /// Silently ignoring them serves wrong bytes, so exportImage rejects
    /// each by name (the T8 quantization precedent).
    /// </summary>
    [Theory]
    [InlineData("mosaicRule", "%7B%22mosaicMethod%22%3A%22esriMosaicNorthwest%22%7D")]
    [InlineData("renderingRule", "%7B%22rasterFunction%22%3A%22Stretch%22%7D")]
    [InlineData("bandIds", "0")]
    public async Task Export_rejects_unhonoured_raster_selection_parameters(string name, string value)
    {
        var client = await ImageServiceAsync(_factory, Name, Dataset);

        var response = await client.GetAsync(
            $"{Root}/{Name}/ImageServer/exportImage?f=json&bbox=0,0,{Width},{Height}&size=4,3&{name}={value}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains(name, error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// T-015 closeout: the same raster-selection parameters are rejected by
    /// name on the ImageServer catalog query (it shares the feature-query
    /// parse path, where they would otherwise be silently ignored).
    /// </summary>
    [Theory]
    [InlineData("mosaicRule", "%7B%22mosaicMethod%22%3A%22esriMosaicNorthwest%22%7D")]
    [InlineData("renderingRule", "%7B%22rasterFunction%22%3A%22Stretch%22%7D")]
    [InlineData("bandIds", "0")]
    public async Task Catalog_query_rejects_unhonoured_raster_selection_parameters(string name, string value)
    {
        await using var catalogFactory = new ImageFactory(_directory, _rasterPath, CatalogDatasetName, catalog: true);
        var client = await ImageServiceAsync(catalogFactory, CatalogName, CatalogDatasetName);

        var response = await client.GetAsync(
            $"{Root}/{CatalogName}/ImageServer/query?f=json&{name}={value}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Export_applies_nodata_as_transparency()
    {
        await using var colorFactory = new ImageFactory(_directory, _colorPath, ColorDataset, catalog: false);
        var client = await ImageServiceAsync(colorFactory, ColorName, ColorDataset);

        var response = await client.GetAsync(
            $"{Root}/{ColorName}/ImageServer/exportImage?f=image&bbox=0,0,{Width},{Height}&size={Width},{Height}&format=png&noData=0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var image = Image.NewFromBuffer(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(4, image.Bands);
        Assert.Equal(0, image.Getpoint(1, 1)[3]);
        Assert.Equal(255, image.Getpoint(2, 1)[3]);
    }

    [Fact]
    public async Task Query_and_item_reject_a_catalog_less_service()
    {
        var client = await ImageServiceAsync(_factory, Name, Dataset);

        var query = await client.GetAsync($"{Root}/{Name}/ImageServer/query?f=json");
        var item = await client.GetAsync($"{Root}/{Name}/ImageServer/1?f=json");

        Assert.Equal(HttpStatusCode.BadRequest, query.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, item.StatusCode);
    }

    [Fact]
    public async Task Identify_samples_the_pixel_without_a_catalog()
    {
        var client = await ImageServiceAsync(_factory, Name, Dataset);

        var identify = await BodyAsync(await client.GetAsync(
            $"{Root}/{Name}/ImageServer/identify?f=json&geometry=" + Uri.EscapeDataString("{\"x\":2.5,\"y\":1.5}") +
            "&geometryType=esriGeometryPoint"));

        Assert.Equal("66", identify.GetProperty("value").GetString());
        Assert.Equal(2.5, identify.GetProperty("location").GetProperty("x").GetDouble());
        Assert.False(identify.TryGetProperty("catalogItems", out _));
    }

    [Fact]
    public async Task A_catalog_service_lists_items_and_exposes_fields()
    {
        await using var catalogFactory = new ImageFactory(_directory, _rasterPath, CatalogDatasetName, catalog: true);
        var client = await ImageServiceAsync(catalogFactory, CatalogName, CatalogDatasetName);

        var root = await BodyAsync(await client.GetAsync($"{Root}/{CatalogName}/ImageServer?f=json"));
        Assert.Equal("OBJECTID", root.GetProperty("objectIdField").GetString());
        var fields = root.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(["OBJECTID", "Shape", "Name"], fields);

        var list = await BodyAsync(await client.GetAsync($"{Root}/{CatalogName}/ImageServer/query?f=json"));
        var features = list.GetProperty("features").EnumerateArray().ToArray();
        Assert.Equal(2, features.Length);
        Assert.Equal("first", features[0].GetProperty("attributes").GetProperty("Name").GetString());
        Assert.True(features[0].GetProperty("geometry").TryGetProperty("rings", out _));

        var ids = await BodyAsync(await client.GetAsync($"{Root}/{CatalogName}/ImageServer/query?f=json&returnIdsOnly=true"));
        Assert.Equal([7L, 8L], ids.GetProperty("objectIds").EnumerateArray().Select(value => value.GetInt64()).ToArray());

        var item = await BodyAsync(await client.GetAsync($"{Root}/{CatalogName}/ImageServer/7?f=json"));
        Assert.Equal(7, item.GetProperty("attributes").GetProperty("OBJECTID").GetInt64());

        var info = await BodyAsync(await client.GetAsync($"{Root}/{CatalogName}/ImageServer/7/info?f=json"));
        Assert.Equal(Width, info.GetProperty("blockWidth").GetInt32());
        Assert.Equal("U8", info.GetProperty("pixelType").GetString());

        var identify = await BodyAsync(await client.GetAsync(
            $"{Root}/{CatalogName}/ImageServer/identify?f=json&geometry=" + Uri.EscapeDataString("{\"x\":2.5,\"y\":1.5}") +
            "&geometryType=esriGeometryPoint"));
        Assert.Equal(7, identify.GetProperty("objectId").GetInt64());
        Assert.Single(identify.GetProperty("catalogItems").GetProperty("features").EnumerateArray());
    }

    [Fact]
    public async Task Catalog_query_filters_projects_and_orders()
    {
        await using var catalogFactory = new ImageFactory(_directory, _rasterPath, CatalogDatasetName, catalog: true);
        var client = await ImageServiceAsync(catalogFactory, CatalogName, CatalogDatasetName);

        var filtered = await BodyAsync(await client.GetAsync(
            $"{Root}/{CatalogName}/ImageServer/query?f=json&where=" + Uri.EscapeDataString("Name = 'second'") +
            "&outFields=Name&returnGeometry=false"));
        var feature = Assert.Single(filtered.GetProperty("features").EnumerateArray());
        Assert.Equal("second", feature.GetProperty("attributes").GetProperty("Name").GetString());
        Assert.False(feature.TryGetProperty("geometry", out _));

        var ordered = await BodyAsync(await client.GetAsync(
            $"{Root}/{CatalogName}/ImageServer/query?f=json&orderByFields=OBJECTID%20DESC&resultRecordCount=1"));
        var page = Assert.Single(ordered.GetProperty("features").EnumerateArray());
        Assert.Equal(8, page.GetProperty("attributes").GetProperty("OBJECTID").GetInt64());
        Assert.True(ordered.GetProperty("exceededTransferLimit").GetBoolean());

        var count = await BodyAsync(await client.GetAsync($"{Root}/{CatalogName}/ImageServer/query?f=json&returnCountOnly=true"));
        Assert.Equal(2, count.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Catalog_query_rejects_an_unsupported_where()
    {
        await using var catalogFactory = new ImageFactory(_directory, _rasterPath, CatalogDatasetName, catalog: true);
        var client = await ImageServiceAsync(catalogFactory, CatalogName, CatalogDatasetName);

        var badWhere = await client.GetAsync(
            $"{Root}/{CatalogName}/ImageServer/query?f=json&where=" + Uri.EscapeDataString("Name === 'x'"));

        Assert.Equal(HttpStatusCode.BadRequest, badWhere.StatusCode);
    }

    [Fact]
    public async Task Catalog_query_accepts_time_like_the_feature_query()
    {
        // T-022: the temporal surface lives on the shared query path, so the
        // raster catalog accepts time exactly as the Feature Service does.
        await using var catalogFactory = new ImageFactory(_directory, _rasterPath, CatalogDatasetName, catalog: true);
        var client = await ImageServiceAsync(catalogFactory, CatalogName, CatalogDatasetName);

        var time = await client.GetAsync($"{Root}/{CatalogName}/ImageServer/query?f=json&time=1199145600000");

        Assert.Equal(HttpStatusCode.OK, time.StatusCode);
    }

    [Fact]
    public async Task Raster_image_renders_the_named_item()
    {
        await using var catalogFactory = new ImageFactory(_directory, _rasterPath, CatalogDatasetName, catalog: true);
        var client = await ImageServiceAsync(catalogFactory, CatalogName, CatalogDatasetName);

        var response = await client.GetAsync($"{Root}/{CatalogName}/ImageServer/8/image?f=image&size=4,3");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("4", response.Headers.GetValues("X-Raster-Width").Single());
    }

    [Fact]
    public async Task Raster_thumbnail_streams_a_reduced_image()
    {
        await using var catalogFactory = new ImageFactory(_directory, _rasterPath, CatalogDatasetName, catalog: true);
        var client = await ImageServiceAsync(catalogFactory, CatalogName, CatalogDatasetName);

        var response = await client.GetAsync($"{Root}/{CatalogName}/ImageServer/7/thumbnail");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        using var image = Image.NewFromBuffer(await response.Content.ReadAsByteArrayAsync());
        Assert.True(image.Width <= 200 && image.Height <= 200);
    }

    [Fact]
    public async Task Download_is_disabled_until_the_host_opts_in()
    {
        await using var catalogFactory = new ImageFactory(_directory, _rasterPath, CatalogDatasetName, catalog: true);
        var client = await ImageServiceAsync(catalogFactory, CatalogName, CatalogDatasetName);

        var response = await client.GetAsync($"{Root}/{CatalogName}/ImageServer/download?rasterIds=7");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Download_lists_raw_files_and_the_file_resource_streams_them()
    {
        await using var catalogFactory = new ImageFactory(
            _directory, _rasterPath, CatalogDatasetName, catalog: true, allowDownload: true);
        var client = await ImageServiceAsync(catalogFactory, CatalogName, CatalogDatasetName);

        var download = await BodyAsync(await client.GetAsync($"{Root}/{CatalogName}/ImageServer/download?f=json&rasterIds=7"));
        var file = Assert.Single(download.GetProperty("rasterFiles").EnumerateArray());
        Assert.Equal(7, Assert.Single(file.GetProperty("rasterIds").EnumerateArray()).GetInt64());
        Assert.Equal(new FileInfo(_rasterPath).Length, file.GetProperty("size").GetInt64());
        var id = file.GetProperty("id").GetString()!;
        Assert.StartsWith("7~", id, StringComparison.Ordinal);

        var content = await client.GetAsync($"{Root}/{CatalogName}/ImageServer/file?id={Uri.EscapeDataString(id)}");
        Assert.Equal(HttpStatusCode.OK, content.StatusCode);
        Assert.Equal("image/tiff", content.Content.Headers.ContentType?.MediaType);
        Assert.Equal(File.ReadAllBytes(_rasterPath), await content.Content.ReadAsByteArrayAsync());

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, $"{Root}/{CatalogName}/ImageServer/file?id={Uri.EscapeDataString(id)}");
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 9);
        var range = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(10, (await range.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task Download_rejects_files_over_the_size_cap()
    {
        await using var catalogFactory = new ImageFactory(
            _directory, _rasterPath, CatalogDatasetName, catalog: true, allowDownload: true, maxDownloadBytes: 16);
        var client = await ImageServiceAsync(catalogFactory, CatalogName, CatalogDatasetName);

        var download = await client.GetAsync($"{Root}/{CatalogName}/ImageServer/download?f=json&rasterIds=7");

        Assert.Equal(HttpStatusCode.BadRequest, download.StatusCode);
    }

    private sealed class ImageFactory : WebApplicationFactory<Program>
    {
        private readonly string _directory;
        private readonly string _rasterPath;
        private readonly string _dataset;
        private readonly bool _catalog;
        private readonly bool _allowDownload;
        private readonly long _maxDownloadBytes;
        private readonly int _width;
        private readonly int _height;

        public ImageFactory(
            string directory, string rasterPath, string dataset, bool catalog,
            bool allowDownload = false, long maxDownloadBytes = 0, int width = Width, int height = Height)
        {
            _directory = directory;
            _rasterPath = rasterPath;
            _dataset = dataset;
            _catalog = catalog;
            _allowDownload = allowDownload;
            _maxDownloadBytes = maxDownloadBytes;
            _width = width;
            _height = height;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", System.IO.Path.Combine(_directory, $"publications-{_dataset}.json"));
            builder.UseSetting("Spatial:Raster:Sources:0:Name", _dataset);
            builder.UseSetting("Spatial:Raster:Sources:0:Path", _rasterPath);
            builder.UseSetting("Spatial:Raster:Sources:0:Crs", "EPSG:4326");
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:0", "0");
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:1", "0");
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:2", _width.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:3", _height.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Spatial:Raster:Sources:0:PixelSizeX", "1");
            builder.UseSetting("Spatial:Raster:Sources:0:PixelSizeY", "1");
            builder.UseSetting("Spatial:Raster:Sources:0:Statistics:0", "0");
            builder.UseSetting("Spatial:Raster:Sources:0:Statistics:1", "255");
            builder.UseSetting("Spatial:Raster:Sources:0:Statistics:2", "82.707");
            builder.UseSetting("Spatial:Raster:Sources:0:Statistics:3", "39.838");
            if (_allowDownload)
            {
                builder.UseSetting("Spatial:GeoServices:AllowRasterDownload", "true");
            }

            if (_maxDownloadBytes > 0)
            {
                builder.UseSetting("Spatial:GeoServices:MaxRasterDownloadBytes", _maxDownloadBytes.ToString(CultureInfo.InvariantCulture));
            }

            if (_catalog)
            {
                builder.ConfigureTestServices(services =>
                    services.AddKeyedSingleton<IRasterCatalogue>("raster", (provider, _) => new VipsRasterCatalogue(
                        [CatalogDescriptor(_rasterPath)], provider.GetRequiredService<ICoordinateTransforms>())));
            }
        }
    }

    private static RasterDatasetDescriptor CatalogDescriptor(string itemPath)
    {
        return new RasterDatasetDescriptor(
            CatalogDatasetName,
            itemPath,
            "EPSG:4326",
            new Envelope(0, 0, Width, Height),
            1,
            1,
            "Catalog",
            CatalogAttributes: [new RasterAttributeDescriptor("Name", AttributeKind.String, Nullable: false)],
            Items:
            [
                new RasterCatalogItemDescriptor(
                    7,
                    Footprint(0, 0, Width, Height),
                    itemPath,
                    new Envelope(0, 0, Width, Height),
                    [AttributeValue.FromString("first")]),
                new RasterCatalogItemDescriptor(
                    8,
                    Footprint(4, 3, Width, Height),
                    itemPath,
                    new Envelope(4, 3, Width, Height),
                    [AttributeValue.FromString("second")]),
            ]);
    }

    private static Polygon Footprint(double minX, double minY, double maxX, double maxY) =>
        GeometryFactory.CreatePolygon(
            [
                new Coordinate(minX, minY),
                new Coordinate(maxX, minY),
                new Coordinate(maxX, maxY),
                new Coordinate(minX, maxY),
                new Coordinate(minX, minY),
            ],
            CoordinateReference.Epsg(4326));
}
