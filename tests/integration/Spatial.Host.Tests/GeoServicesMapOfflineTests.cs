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
/// T-041 map offline/async scoping (research/compat/map-service.md §1,
/// research/compat/tiles.md): <c>exportTiles</c> + <c>estimateExportTileSize</c>
/// (map + image variants), the WMTS triple, KML (<c>generateKml</c>,
/// <c>kml</c> image) and the async <c>jobs</c> surface are documented
/// non-goals under the no-job architecture (ADR-0033) — tiles are
/// live-rendered only. Each named operation is rejected by name with a typed
/// <c>invalid.arguments</c> Esri envelope (never a silent fall-through), and
/// the root keeps advertising <c>exportTilesAllowed:false</c>. Red-first:
/// none of these routes exists today.
/// </summary>
public sealed class GeoServicesMapOfflineTests : IDisposable
{
    private static readonly string[] MapServices = ["map"];
    private static readonly string[] ImageServices = ["image"];

    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";
    private const string CityStyle =
        """[{"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#ff0000","circle-radius":6,"circle-opacity":1.0}}]""";

    private const string ImageDataset = "raster.offline";
    private const int RasterWidth = 8;
    private const int RasterHeight = 6;

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-map-offline-").FullName;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly WebApplicationFactory<Program> _imageFactory;

    public GeoServicesMapOfflineTests()
    {
        var rasterPath = Path.Combine(_directory, "offline.tif");
        var pixels = new byte[RasterWidth * RasterHeight];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % 256);
        }

        using var image = Image.NewFromMemory(pixels, RasterWidth, RasterHeight, 1, Enums.BandFormat.Uchar);
        image.WriteToFile(rasterPath);
        _factory = new MapFactory(Path.Combine(_directory, "publications.json"));
        _imageFactory = new OfflineImageFactory(_directory, rasterPath, ImageDataset);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _imageFactory.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<HttpClient> MapServiceAsync()
    {
        var client = _factory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name = "world",
            store = "demo",
            services = MapServices,
            layers = new[] { new { dataset = "demo.cities", layerId = 0, name = "Cities", style = CityStyle } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/maps/world")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private async Task<HttpClient> ImageServiceAsync()
    {
        var client = _imageFactory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name = "imagery",
            store = "raster",
            services = ImageServices,
            layers = new[] { new { dataset = ImageDataset, layerId = 0, name = "Imagery", kind = "image" } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/maps/imagery")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<JsonElement> RejectAsync(HttpResponseMessage response, string operation)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(400, body.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains(operation, body.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
        return body;
    }

    [Fact]
    public async Task Export_tiles_is_rejected_by_name()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/world/MapServer/exportTiles?f=json");

        await RejectAsync(response, "exportTiles");
    }

    [Fact]
    public async Task Estimate_export_tile_size_is_rejected_by_name()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/world/MapServer/estimateExportTileSize?f=json");

        await RejectAsync(response, "estimateExportTileSize");
    }

    [Fact]
    public async Task Image_export_tiles_is_rejected_by_name()
    {
        var client = await ImageServiceAsync();

        var response = await client.GetAsync($"{Root}/imagery/ImageServer/exportTiles?f=json");

        await RejectAsync(response, "exportTiles");
    }

    [Fact]
    public async Task Image_estimate_export_tile_size_is_rejected_by_name()
    {
        var client = await ImageServiceAsync();

        var response = await client.GetAsync($"{Root}/imagery/ImageServer/estimateExportTileSize?f=json");

        await RejectAsync(response, "estimateExportTileSize");
    }

    [Fact]
    public async Task Wmts_capabilities_are_rejected_by_name()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/world/MapServer/WMTS/1.0.0/WMTSCapabilities.xml");

        await RejectAsync(response, "WMTS");
    }

    [Fact]
    public async Task The_wmts_base_is_rejected_by_name()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/world/MapServer/WMTS?f=json");

        await RejectAsync(response, "WMTS");
    }

    [Fact]
    public async Task Generate_kml_is_rejected_by_name()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/world/MapServer/generateKml?f=json");

        await RejectAsync(response, "generateKml");
    }

    [Fact]
    public async Task The_kml_image_is_rejected_by_name()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/world/MapServer/kml/mapImage.kmz");

        await RejectAsync(response, "kml");
    }

    [Fact]
    public async Task Async_jobs_are_rejected_by_name()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/world/MapServer/jobs?f=json");

        var body = await RejectAsync(response, "job");
        Assert.Contains("cancellable", body.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_named_job_poll_is_rejected_by_name()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/world/MapServer/jobs/abc123?f=json");

        await RejectAsync(response, "job");
    }

    [Fact]
    public async Task Offline_rejects_accept_post()
    {
        var client = await MapServiceAsync();

        var response = await client.PostAsync($"{Root}/world/MapServer/exportTiles?f=json", new StringContent(string.Empty));

        await RejectAsync(response, "exportTiles");
    }

    [Fact]
    public async Task An_offline_operation_on_an_unknown_service_is_not_found()
    {
        var client = await MapServiceAsync();

        var response = await client.GetAsync($"{Root}/nosuch/MapServer/exportTiles?f=json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class MapFactory(string publicationsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", publicationsPath);
        }
    }

    private sealed class OfflineImageFactory(string directory, string rasterPath, string dataset)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", Path.Combine(directory, "publications-image.json"));
            builder.UseSetting("Spatial:Raster:Sources:0:Name", dataset);
            builder.UseSetting("Spatial:Raster:Sources:0:Path", rasterPath);
            builder.UseSetting("Spatial:Raster:Sources:0:Crs", "EPSG:4326");
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:0", "0");
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:1", "0");
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:2", RasterWidth.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:3", RasterHeight.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Spatial:Raster:Sources:0:PixelSizeX", "1");
            builder.UseSetting("Spatial:Raster:Sources:0:PixelSizeY", "1");
            builder.ConfigureTestServices(services =>
                services.AddKeyedSingleton<IRasterCatalogue>("raster", (provider, _) => new VipsRasterCatalogue(
                    [new RasterDatasetDescriptor(
                        dataset, rasterPath, "EPSG:4326", new Envelope(0, 0, RasterWidth, RasterHeight),
                        1, 1, "Fixture service")],
                    provider.GetRequiredService<ICoordinateTransforms>())));
        }
    }
}
