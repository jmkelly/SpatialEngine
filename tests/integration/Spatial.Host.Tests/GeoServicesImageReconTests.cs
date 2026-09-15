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
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Imagery.Vips.Raster;

namespace Spatial.Host.Tests;

/// <summary>
/// T-027 ImageServer reconnaissance: one test per operation recording the
/// current behaviour of the catalog-backed ImageServer before extending it
/// (mosaicRule/renderingRule/bandIds handling stays thin). The export and
/// download/file surfaces are pinned alongside query/identify so a later
/// change has a baseline to diff against.
/// </summary>
public sealed class GeoServicesImageReconTests : IDisposable
{
    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";
    private const string Service = "recon";
    private const string Dataset = "raster.recon";
    private const int Width = 8;
    private const int Height = 6;

    private static readonly string[] ExpectedFields = ["OBJECTID", "Shape", "Name"];

    private static readonly string[] ImageServices = ["image"];

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-image-recon-").FullName;
    private readonly string _rasterPath;
    private readonly WebApplicationFactory<Program> _factory;

    public GeoServicesImageReconTests()
    {
        _rasterPath = Path.Combine(_directory, "recon.tif");
        var pixels = new byte[Width * Height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % 256);
        }

        using var image = Image.NewFromMemory(pixels, Width, Height, 1, Enums.BandFormat.Uchar);
        image.WriteToFile(_rasterPath);
        _factory = new ReconFactory(_directory, _rasterPath, Dataset);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<HttpClient> ServiceAsync()
    {
        var client = _factory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name = Service,
            store = "raster",
            services = ImageServices,
            layers = new[] { new { dataset = Dataset, layerId = 0, name = Service, kind = "image" } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/maps/{Service}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task Recon_root_reports_catalog_metadata()
    {
        var client = await ServiceAsync();

        var root = await BodyAsync(await client.GetAsync($"{Root}/{Service}/ImageServer?f=json"));

        Assert.Equal("OBJECTID", root.GetProperty("objectIdField").GetString());
        Assert.Equal(ExpectedFields,
            root.GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task Recon_export_image_streams_bytes_and_json_href()
    {
        var client = await ServiceAsync();

        var bytes = await client.GetAsync($"{Root}/{Service}/ImageServer/exportImage?f=image&bbox=0,0,8,6&size=4,3&format=png");
        Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
        Assert.Equal("image/png", bytes.Content.Headers.ContentType?.MediaType);

        var href = await BodyAsync(await client.GetAsync($"{Root}/{Service}/ImageServer/exportImage?f=json&bbox=0,0,8,6&size=4,3&format=png"));
        Assert.True(href.TryGetProperty("href", out _));
    }

    [Fact]
    public async Task Recon_identify_samples_the_catalog_item()
    {
        var client = await ServiceAsync();

        var identify = await BodyAsync(await client.GetAsync(
            $"{Root}/{Service}/ImageServer/identify?f=json&geometry=" + Uri.EscapeDataString("{\"x\":2.5,\"y\":1.5}") +
            "&geometryType=esriGeometryPoint"));

        Assert.Equal(7, identify.GetProperty("objectId").GetInt64());
        Assert.Single(identify.GetProperty("catalogItems").GetProperty("features").EnumerateArray());
    }

    [Fact]
    public async Task Recon_query_lists_counts_and_pages_the_catalog()
    {
        var client = await ServiceAsync();

        var list = await BodyAsync(await client.GetAsync($"{Root}/{Service}/ImageServer/query?f=json"));
        Assert.Equal(2, list.GetProperty("features").GetArrayLength());

        var count = await BodyAsync(await client.GetAsync($"{Root}/{Service}/ImageServer/query?f=json&returnCountOnly=true"));
        Assert.Equal(2, count.GetProperty("count").GetInt32());

        var page = await BodyAsync(await client.GetAsync(
            $"{Root}/{Service}/ImageServer/query?f=json&orderByFields=OBJECTID%20DESC&resultRecordCount=1"));
        Assert.Equal(8, page.GetProperty("features")[0].GetProperty("attributes").GetProperty("OBJECTID").GetInt64());
        Assert.True(page.GetProperty("exceededTransferLimit").GetBoolean());
    }

    [Fact]
    public async Task Recon_raster_item_info_image_and_thumbnail()
    {
        var client = await ServiceAsync();

        var item = await BodyAsync(await client.GetAsync($"{Root}/{Service}/ImageServer/7?f=json"));
        Assert.Equal(7, item.GetProperty("attributes").GetProperty("OBJECTID").GetInt64());

        var info = await BodyAsync(await client.GetAsync($"{Root}/{Service}/ImageServer/7/info?f=json"));
        Assert.Equal("U8", info.GetProperty("pixelType").GetString());

        var image = await client.GetAsync($"{Root}/{Service}/ImageServer/8/image?f=image&size=4,3");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);

        var thumbnail = await client.GetAsync($"{Root}/{Service}/ImageServer/7/thumbnail");
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.Equal("image/png", thumbnail.Content.Headers.ContentType?.MediaType);

        var missing = await client.GetAsync($"{Root}/{Service}/ImageServer/999?f=json");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Recon_download_and_file_stay_disabled_without_opt_in()
    {
        var client = await ServiceAsync();

        var download = await client.GetAsync($"{Root}/{Service}/ImageServer/download?rasterIds=7");
        Assert.Equal(HttpStatusCode.BadRequest, download.StatusCode);

        var file = await client.GetAsync($"{Root}/{Service}/ImageServer/file?id=7~x");
        Assert.Equal(HttpStatusCode.BadRequest, file.StatusCode);
    }

    private sealed class ReconFactory(string directory, string rasterPath, string dataset) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", Path.Combine(directory, "publications-recon.json"));
            builder.UseSetting("Spatial:Raster:Sources:0:Name", dataset);
            builder.UseSetting("Spatial:Raster:Sources:0:Path", rasterPath);
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
            builder.ConfigureTestServices(services =>
                services.AddKeyedSingleton<IRasterCatalogue>("raster", (provider, _) => new VipsRasterCatalogue(
                    [new RasterDatasetDescriptor(
                        dataset,
                        rasterPath,
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
                                rasterPath,
                                new Envelope(0, 0, Width, Height),
                                [AttributeValue.FromString("first")]),
                            new RasterCatalogItemDescriptor(
                                8,
                                Footprint(4, 3, Width, Height),
                                rasterPath,
                                new Envelope(4, 3, Width, Height),
                                [AttributeValue.FromString("second")]),
                        ])],
                    provider.GetRequiredService<ICoordinateTransforms>())));
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
}
