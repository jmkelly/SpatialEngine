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
/// T-056 red replay tests: the ImageServer serves authored metadata XML —
/// the service-level <c>metadata</c> resource from the map's authored
/// document and the per-item <c>{rasterId}/metadata</c> resource from the
/// catalog item's authored document (the Esri reference returns authored
/// ISO/FGDC XML at both levels, ADR-0068). Missing authoring is a typed
/// <c>not.found</c>, never an invented document.
/// </summary>
public sealed class GeoServicesImageMetadataTests : IDisposable
{
    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";
    private const int Width = 8;
    private const int Height = 6;

    private const string ServiceXml = """
        <MD_Metadata xmlns="http://www.isotc211.org/2005/gmd"><title>Fixture service metadata</title></MD_Metadata>
        """;

    private const string ItemXml = """
        <MD_Metadata xmlns="http://www.isotc211.org/2005/gmd"><title>Item seven metadata</title></MD_Metadata>
        """;

    private static readonly string[] ImageServices = ["image"];

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-image-metadata-").FullName;
    private readonly string _rasterPath;

    public GeoServicesImageMetadataTests()
    {
        _rasterPath = Path.Combine(_directory, "metadata.tif");
        var pixels = new byte[Width * Height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % 256);
        }

        using var image = Image.NewFromMemory(pixels, Width, Height, 1, Enums.BandFormat.Uchar);
        image.WriteToFile(_rasterPath);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Service_metadata_serves_the_authored_xml()
    {
        await using var factory = new MetadataFactory(_directory, _rasterPath, "raster.metaxml", itemMetadata: null);
        var client = await ImageServiceAsync(factory, "metaxml", "raster.metaxml", ServiceXml);

        foreach (var suffix in new[] { string.Empty, "?f=xml", "?f=XML" })
        {
            var response = await client.GetAsync($"{Root}/metaxml/ImageServer/metadata{suffix}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(ServiceXml, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Service_metadata_without_authored_xml_is_not_found()
    {
        await using var factory = new MetadataFactory(_directory, _rasterPath, "raster.nometa", itemMetadata: null);
        var client = await ImageServiceAsync(factory, "nometa", "raster.nometa", serviceMetadata: null);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"{Root}/nometa/ImageServer/metadata")).StatusCode);
    }

    [Fact]
    public async Task Service_metadata_rejects_a_json_format()
    {
        await using var factory = new MetadataFactory(_directory, _rasterPath, "raster.metafmt", itemMetadata: null);
        var client = await ImageServiceAsync(factory, "metafmt", "raster.metafmt", ServiceXml);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync($"{Root}/metafmt/ImageServer/metadata?f=json")).StatusCode);
    }

    [Fact]
    public async Task Raster_metadata_serves_the_authored_item_xml()
    {
        await using var factory = new MetadataFactory(_directory, _rasterPath, "raster.itemmeta", itemMetadata: ItemXml);
        var client = await ImageServiceAsync(factory, "itemmeta", "raster.itemmeta", ServiceXml);

        var response = await client.GetAsync($"{Root}/itemmeta/ImageServer/7/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ItemXml, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Raster_metadata_without_item_xml_is_not_found()
    {
        await using var factory = new MetadataFactory(_directory, _rasterPath, "raster.noitemmeta", itemMetadata: null);
        var client = await ImageServiceAsync(factory, "noitemmeta", "raster.noitemmeta", ServiceXml);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"{Root}/noitemmeta/ImageServer/7/metadata")).StatusCode);
    }

    [Fact]
    public async Task Raster_metadata_of_an_unknown_item_is_not_found()
    {
        await using var factory = new MetadataFactory(_directory, _rasterPath, "raster.unknownitem", itemMetadata: ItemXml);
        var client = await ImageServiceAsync(factory, "unknownitem", "raster.unknownitem", ServiceXml);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"{Root}/unknownitem/ImageServer/99/metadata")).StatusCode);
    }

    [Fact]
    public async Task Raster_metadata_without_a_catalog_is_rejected()
    {
        await using var factory = new MetadataFactory(_directory, _rasterPath, "raster.plainmeta", itemMetadata: null, catalog: false);
        var client = await ImageServiceAsync(factory, "plainmeta", "raster.plainmeta", ServiceXml);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync($"{Root}/plainmeta/ImageServer/7/metadata")).StatusCode);
    }

    [Fact]
    public async Task Service_metadata_of_an_unknown_service_is_not_found()
    {
        await using var factory = new MetadataFactory(_directory, _rasterPath, "raster.knownmeta", itemMetadata: null);
        var client = await ImageServiceAsync(factory, "knownmeta", "raster.knownmeta", ServiceXml);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"{Root}/nosuchsvc/ImageServer/metadata")).StatusCode);
    }

    [Fact]
    public async Task Raster_metadata_rejects_a_json_format()
    {
        await using var factory = new MetadataFactory(_directory, _rasterPath, "raster.itemfmt", itemMetadata: ItemXml);
        var client = await ImageServiceAsync(factory, "itemfmt", "raster.itemfmt", ServiceXml);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync($"{Root}/itemfmt/ImageServer/7/metadata?f=json")).StatusCode);
    }

    private static async Task<HttpClient> ImageServiceAsync(
        WebApplicationFactory<Program> factory, string service, string dataset, string? serviceMetadata)
    {
        var client = factory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name = service,
            store = "raster",
            services = ImageServices,
            layers = new[] { new { dataset, layerId = 0, name = service, kind = "image" } },
            metadataXml = serviceMetadata,
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

    private sealed class MetadataFactory(
        string directory, string rasterPath, string dataset, string? itemMetadata, bool catalog = true)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", Path.Combine(directory, $"publications-{dataset}.json"));
            builder.UseSetting("Spatial:Raster:Sources:0:Name", dataset);
            builder.UseSetting("Spatial:Raster:Sources:0:Path", rasterPath);
            builder.UseSetting("Spatial:Raster:Sources:0:Crs", "EPSG:4326");
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:0", "0");
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:1", "0");
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:2", Width.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Spatial:Raster:Sources:0:Extent:3", Height.ToString(CultureInfo.InvariantCulture));
            builder.UseSetting("Spatial:Raster:Sources:0:PixelSizeX", "1");
            builder.UseSetting("Spatial:Raster:Sources:0:PixelSizeY", "1");
            builder.ConfigureTestServices(services =>
                services.AddKeyedSingleton<IRasterCatalogue>("raster", (provider, _) => new VipsRasterCatalogue(
                    [Descriptor()], provider.GetRequiredService<ICoordinateTransforms>())));
        }

        private RasterDatasetDescriptor Descriptor()
        {
            if (!catalog)
            {
                return new RasterDatasetDescriptor(
                    dataset,
                    rasterPath,
                    "EPSG:4326",
                    new Envelope(0, 0, Width, Height),
                    1,
                    1,
                    "Fixture service");
            }

            return new(
                dataset,
                rasterPath,
                "EPSG:4326",
                new Envelope(0, 0, Width, Height),
                1,
                1,
                "Fixture service",
                CatalogAttributes: [new RasterAttributeDescriptor("Name", AttributeKind.String, Nullable: false)],
                Items:
                [
                    new RasterCatalogItemDescriptor(
                        7,
                        Footprint(0, 0, Width, Height),
                        rasterPath,
                        new Envelope(0, 0, Width, Height),
                        [AttributeValue.FromString("first")],
                        MetadataXml: itemMetadata),
                    new RasterCatalogItemDescriptor(
                        8,
                        Footprint(4, 3, Width, Height),
                        rasterPath,
                        new Envelope(4, 3, Width, Height),
                        [AttributeValue.FromString("second")]),
                ]);
        }

        private static Polygon Footprint(double minX, double minY, double maxX, double maxY) =>
            GeometryFactory.CreatePolygon(
                [
                    new Coordinate(minX, minY),
                    new Coordinate(maxX, minY),
                    new Coordinate(minX, maxY),
                    new Coordinate(minX, minY),
                ],
                CoordinateReference.Epsg(4326));
    }
}
