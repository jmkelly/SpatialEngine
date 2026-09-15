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
/// T-043 red replay tests: the ImageServer root must carry truthful
/// capability flags (each proved by the behaviour it names, T-019 pattern),
/// and the mensuration / multidimensional / catalog-write operations must be
/// rejected by name instead of falling through to a generic 404.
/// Raster functions, mosaic methods and mensuration are non-goals already
/// rejected on export/query; the flags below say so honestly.
/// </summary>
public sealed class GeoServicesImageHonestyTests : IDisposable
{
    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";
    private const int Width = 8;
    private const int Height = 6;

    private static readonly string[] ImageServices = ["image"];

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-image-honesty-").FullName;
    private readonly string _rasterPath;
    private readonly string _floatPath;

    public GeoServicesImageHonestyTests()
    {
        _rasterPath = Path.Combine(_directory, "honesty.tif");
        var pixels = new byte[Width * Height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i % 256);
        }

        using var image = Image.NewFromMemory(pixels, Width, Height, 1, Enums.BandFormat.Uchar);
        image.WriteToFile(_rasterPath);

        _floatPath = Path.Combine(_directory, "honesty-float.tif");
        var floats = new float[Width * Height];
        for (var i = 0; i < floats.Length; i++)
        {
            floats[i] = i;
        }

        using var floatImage = Image.NewFromMemory(floats, Width, Height, 1, Enums.BandFormat.Float);
        floatImage.WriteToFile(_floatPath);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Root_advertises_honest_capability_flags()
    {
        await using var factory = new HonestyFactory(
            _directory, _rasterPath, "raster.honest", catalog: true, statistics: true, attributeTable: true,
            maxDownloadBytes: 12345, maxDownloadFiles: 7);
        var client = await ImageServiceAsync(factory, "honest", "raster.honest");

        var root = await BodyAsync(await client.GetAsync($"{Root}/honest/ImageServer?f=json"));

        Assert.False(root.GetProperty("allowRasterFunction").GetBoolean());
        Assert.Empty(root.GetProperty("rasterFunctionInfos").EnumerateArray());
        Assert.Equal("None", root.GetProperty("allowedMosaicMethods").GetString());
        Assert.Equal("None", root.GetProperty("defaultMosaicMethod").GetString());
        Assert.Equal("First", root.GetProperty("mosaicOperator").GetString());
        Assert.Equal("None", root.GetProperty("mensurationCapabilities").GetString());
        Assert.False(root.GetProperty("hasColormap").GetBoolean());
        Assert.True(root.GetProperty("hasHistograms").GetBoolean());
        Assert.True(root.GetProperty("hasRasterAttributeTable").GetBoolean());
        Assert.Equal(7, root.GetProperty("maxDownloadImageCount").GetInt32());
        Assert.Equal(12345, root.GetProperty("maxDownloadSizeLimit").GetInt64());
        Assert.Equal("esriImageServiceSourceTypeDataset", root.GetProperty("serviceSourceType").GetString());
    }

    [Fact]
    public async Task Root_reports_a_missing_attribute_table_honestly()
    {
        await using var factory = new HonestyFactory(
            _directory, _rasterPath, "raster.noratflag", catalog: false, statistics: true, attributeTable: false);
        var client = await ImageServiceAsync(factory, "noratflag", "raster.noratflag");

        var root = await BodyAsync(await client.GetAsync($"{Root}/noratflag/ImageServer?f=json"));

        Assert.False(root.GetProperty("hasRasterAttributeTable").GetBoolean());
        Assert.True(root.GetProperty("hasHistograms").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Root}/noratflag/ImageServer/rasterAttributeTable?f=json")).StatusCode);
    }

    [Fact]
    public async Task Root_reports_histograms_for_a_float_raster()
    {
        await using var factory = new HonestyFactory(
            _directory, _floatPath, "raster.floathist", catalog: false, statistics: false, attributeTable: false);
        var client = await ImageServiceAsync(factory, "floathist", "raster.floathist");

        var root = await BodyAsync(await client.GetAsync($"{Root}/floathist/ImageServer?f=json"));

        Assert.Equal("F32", root.GetProperty("pixelType").GetString());
        Assert.True(root.GetProperty("hasHistograms").GetBoolean());

        var geometry = Uri.EscapeDataString("{\"xmin\":0,\"ymin\":0,\"xmax\":8,\"ymax\":6,\"spatialReference\":{\"wkid\":4326}}");
        var histograms = await BodyAsync(await client.GetAsync(
            $"{Root}/floathist/ImageServer/computeHistograms?f=json&geometryType=esriGeometryEnvelope&geometry={geometry}"));
        var band = Assert.Single(histograms.GetProperty("histograms").EnumerateArray());
        Assert.Equal(256, band.GetProperty("size").GetInt32());
        Assert.Equal(0, band.GetProperty("min").GetDouble());
        Assert.Equal(47, band.GetProperty("max").GetDouble());
        var counts = band.GetProperty("counts").EnumerateArray().Select(value => value.GetInt64()).ToArray();
        Assert.Equal(Width * Height, counts.Sum());
        Assert.Equal(1, counts[0]);
        Assert.Equal(1, counts[255]);
    }

    /// <summary>
    /// Mensuration needs sensor models the raster catalog does not carry, so
    /// every mensuration operation is rejected by name (research S3
    /// <c>measure/</c>, <c>measure-from-image/</c>, <c>compute-angles/</c>,
    /// <c>project-…</c>, <c>image-to-map…</c>, <c>query-gps</c>,
    /// <c>query-boundary</c>).
    /// </summary>
    [Theory]
    [InlineData("measure")]
    [InlineData("measureFromImage")]
    [InlineData("computeAngles")]
    [InlineData("project")]
    [InlineData("projectImage")]
    [InlineData("imageToMap")]
    [InlineData("mapToImage")]
    [InlineData("queryGPSInfo")]
    [InlineData("queryBoundary")]
    public async Task Mensuration_operations_are_rejected_by_name(string operation)
    {
        await using var factory = new HonestyFactory(
            _directory, _rasterPath, "raster.honest", catalog: true, statistics: true, attributeTable: true);
        var client = await ImageServiceAsync(factory, "honest", "raster.honest");

        var response = await client.GetAsync($"{Root}/honest/ImageServer/{operation}?f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains(operation, error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Multidimensional rasters are not supported (research S3
    /// <c>multidimensional-info/</c>, <c>slices/</c>), so both operations are
    /// rejected by name.
    /// </summary>
    [Theory]
    [InlineData("multidimensionalInfo")]
    [InlineData("slices")]
    public async Task Multidimensional_operations_are_rejected_by_name(string operation)
    {
        await using var factory = new HonestyFactory(
            _directory, _rasterPath, "raster.honest", catalog: true, statistics: true, attributeTable: true);
        var client = await ImageServiceAsync(factory, "honest", "raster.honest");

        var response = await client.GetAsync($"{Root}/honest/ImageServer/{operation}?f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains(operation, error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The raster catalog is read-only (ingest is the neutral write path,
    /// ADR-0041), so every catalog-write operation is rejected by name.
    /// </summary>
    [Theory]
    [InlineData("addRasters")]
    [InlineData("deleteRasters")]
    [InlineData("updateRaster")]
    [InlineData("uploads")]
    [InlineData("upload")]
    public async Task Catalog_writes_are_rejected_by_name(string operation)
    {
        await using var factory = new HonestyFactory(
            _directory, _rasterPath, "raster.honest", catalog: true, statistics: true, attributeTable: true);
        var client = await ImageServiceAsync(factory, "honest", "raster.honest");

        var response = await client.GetAsync($"{Root}/honest/ImageServer/{operation}?f=json");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains(operation, error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejected_operations_answer_post_like_the_served_surface()
    {
        await using var factory = new HonestyFactory(
            _directory, _rasterPath, "raster.honest", catalog: true, statistics: true, attributeTable: true);
        var client = await ImageServiceAsync(factory, "honest", "raster.honest");

        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json" });
        var response = await client.PostAsync($"{Root}/honest/ImageServer/measure", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(400, error.GetProperty("code").GetInt32());
    }

    private static async Task<HttpClient> ImageServiceAsync(WebApplicationFactory<Program> factory, string service, string dataset)
    {
        var client = factory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name = service,
            store = "raster",
            services = ImageServices,
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

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement;
    }

    private sealed class HonestyFactory(
        string directory, string rasterPath, string dataset, bool catalog, bool statistics, bool attributeTable,
        long maxDownloadBytes = 0, int maxDownloadFiles = 0)
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
            if (maxDownloadBytes > 0)
            {
                builder.UseSetting("Spatial:GeoServices:MaxRasterDownloadBytes", maxDownloadBytes.ToString(CultureInfo.InvariantCulture));
            }

            if (maxDownloadFiles > 0)
            {
                builder.UseSetting("Spatial:GeoServices:MaxRasterDownloadFiles", maxDownloadFiles.ToString(CultureInfo.InvariantCulture));
            }

            builder.ConfigureTestServices(services =>
                services.AddKeyedSingleton<IRasterCatalogue>("raster", (provider, _) => new VipsRasterCatalogue(
                    [Descriptor()], provider.GetRequiredService<ICoordinateTransforms>())));
        }

        private RasterDatasetDescriptor Descriptor()
        {
            IReadOnlyList<RasterBandStatistics>? stats = statistics ? [new RasterBandStatistics(0, 255, 82.707, 39.838)] : null;
            var table = attributeTable
                ? new RasterAttributeTable(
                    "OBJECTID",
                    [new RasterAttributeField("OID", AttributeKind.Int64, Nullable: false),
                     new RasterAttributeField("Value", AttributeKind.Int64, Nullable: false),
                     new RasterAttributeField("ClassName", AttributeKind.String, Length: 50)],
                    [[AttributeValue.FromInt64(0), AttributeValue.FromInt64(0), AttributeValue.FromString("Background")],
                     [AttributeValue.FromInt64(1), AttributeValue.FromInt64(87), AttributeValue.FromString("Bright")]])
                : null;
            if (!catalog)
            {
                return new RasterDatasetDescriptor(
                    dataset, rasterPath, "EPSG:4326", new Envelope(0, 0, Width, Height), 1, 1,
                    "Fixture service", Statistics: stats, AttributeTable: table);
            }

            return new RasterDatasetDescriptor(
                dataset,
                rasterPath,
                "EPSG:4326",
                new Envelope(0, 0, Width, Height),
                1,
                1,
                "Fixture service",
                Statistics: stats,
                AttributeTable: table,
                CatalogAttributes: [new RasterAttributeDescriptor("Name", AttributeKind.String, Nullable: false)],
                Items:
                [
                    new RasterCatalogItemDescriptor(
                        7,
                        Footprint(0, 0, Width, Height),
                        rasterPath,
                        new Envelope(0, 0, Width, Height),
                        [AttributeValue.FromString("first")]),
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
