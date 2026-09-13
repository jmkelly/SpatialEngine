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
        Assert.Single(features);
        Assert.Equal("first", features[0].GetProperty("attributes").GetProperty("Name").GetString());
        Assert.True(features[0].GetProperty("geometry").TryGetProperty("rings", out _));

        var ids = await BodyAsync(await client.GetAsync($"{Root}/{CatalogName}/ImageServer/query?f=json&returnIdsOnly=true"));
        Assert.Equal(7, ids.GetProperty("objectIds")[0].GetInt64());

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

    private sealed class ImageFactory : WebApplicationFactory<Program>
    {
        private readonly string _directory;
        private readonly string _rasterPath;
        private readonly string _dataset;
        private readonly bool _catalog;

        public ImageFactory(string directory, string rasterPath, string dataset, bool catalog)
        {
            _directory = directory;
            _rasterPath = rasterPath;
            _dataset = dataset;
            _catalog = catalog;
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
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:2", Width.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:3", Height.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Spatial:Raster:Sources:0:PixelSizeX", "1");
            builder.UseSetting("Spatial:Raster:Sources:0:PixelSizeY", "1");
            builder.UseSetting("Spatial:Raster:Sources:0:Statistics:0", "0");
            builder.UseSetting("Spatial:Raster:Sources:0:Statistics:1", "255");
            builder.UseSetting("Spatial:Raster:Sources:0:Statistics:2", "82.707");
            builder.UseSetting("Spatial:Raster:Sources:0:Statistics:3", "39.838");
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
        var footprint = GeometryFactory.CreatePolygon(
            [
                new Coordinate(0, 0),
                new Coordinate(Width, 0),
                new Coordinate(Width, Height),
                new Coordinate(0, Height),
                new Coordinate(0, 0),
            ],
            CoordinateReference.Epsg(4326));
        var item = new RasterCatalogItemDescriptor(
            7,
            footprint,
            itemPath,
            new Envelope(0, 0, Width, Height),
            [AttributeValue.FromString("first")]);
        return new RasterDatasetDescriptor(
            CatalogDatasetName,
            itemPath,
            "EPSG:4326",
            new Envelope(0, 0, Width, Height),
            1,
            1,
            "Catalog",
            CatalogAttributes: [new RasterAttributeDescriptor("Name", AttributeKind.String, Nullable: false)],
            Items: [item]);
    }
}
