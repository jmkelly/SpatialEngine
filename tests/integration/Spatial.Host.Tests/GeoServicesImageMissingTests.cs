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
/// T-042 red replay tests: the ImageServer resources missing from
/// <c>GeoServicesEndpoints.Images.cs</c> — <c>legend</c>, <c>find</c>, the
/// stored-statistics <c>statistics</c> resource and per-item statistics,
/// computed <c>computeHistograms</c>, the configured
/// <c>rasterAttributeTable</c>, and the service-level <c>thumbnail</c> and
/// <c>metadata</c>. Shapes replay the Esri reference (legend-image-service,
/// compute-histograms, raster-attribute-table, statistics) and the
/// NLCDLandCover2001/​CharlotteLAS ground truth.
/// </summary>
public sealed class GeoServicesImageMissingTests : IDisposable
{
    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";
    private const int Width = 8;
    private const int Height = 6;

    private static readonly string[] ImageServices = ["image"];

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-image-missing-").FullName;
    private readonly string _rasterPath;

    public GeoServicesImageMissingTests()
    {
        _rasterPath = Path.Combine(_directory, "missing.tif");
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
    public async Task Legend_returns_band_entries_with_rendered_swatches()
    {
        await using var factory = new MissingFactory(_directory, _rasterPath, "raster.legend", catalog: true, statistics: true, attributeTable: false);
        var client = await ImageServiceAsync(factory, "legend", "raster.legend");

        var root = await BodyAsync(await client.GetAsync($"{Root}/legend/ImageServer?f=json"));
        var legend = await BodyAsync(await client.GetAsync($"{Root}/legend/ImageServer/legend?f=json"));

        var layer = Assert.Single(legend.GetProperty("layers").EnumerateArray());
        Assert.Equal(0, layer.GetProperty("layerId").GetInt32());
        Assert.Equal(root.GetProperty("name").GetString(), layer.GetProperty("layerName").GetString());
        Assert.Equal("Raster Layer", layer.GetProperty("layerType").GetString());
        var entry = Assert.Single(layer.GetProperty("legend").EnumerateArray());
        Assert.Equal("Band_1", entry.GetProperty("label").GetString());
        Assert.Equal("image/png", entry.GetProperty("contentType").GetString());
        Assert.Equal(20, entry.GetProperty("width").GetInt32());
        Assert.Equal(20, entry.GetProperty("height").GetInt32());
        Assert.Equal(32, entry.GetProperty("url").GetString()!.Length);
        using var swatch = Image.NewFromBuffer(Convert.FromBase64String(entry.GetProperty("imageData").GetString()!));
        Assert.Equal(20, swatch.Width);
        Assert.Equal(20, swatch.Height);
    }

    [Theory]
    [InlineData("renderingRule", "%7B%22rasterFunction%22%3A%22Stretch%22%7D")]
    [InlineData("bandIds", "5")]
    public async Task Legend_rejects_unhonoured_or_unknown_bands(string name, string value)
    {
        await using var factory = new MissingFactory(_directory, _rasterPath, "raster.legend", catalog: true, statistics: true, attributeTable: false);
        var client = await ImageServiceAsync(factory, "legend", "raster.legend");

        var response = await client.GetAsync($"{Root}/legend/ImageServer/legend?f=json&{name}={value}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Find_matches_catalog_names_case_insensitively()
    {
        await using var factory = new MissingFactory(_directory, _rasterPath, "raster.find", catalog: true, statistics: true, attributeTable: false);
        var client = await ImageServiceAsync(factory, "findsvc", "raster.find");

        var found = await BodyAsync(await client.GetAsync($"{Root}/findsvc/ImageServer/find?f=json&searchText=fir"));
        var hit = Assert.Single(found.GetProperty("results").EnumerateArray());
        Assert.Equal(0, hit.GetProperty("layerId").GetInt32());
        Assert.Equal("Name", hit.GetProperty("foundFieldName").GetString());
        Assert.Equal("first", hit.GetProperty("value").GetString());
        Assert.Equal(7, hit.GetProperty("attributes").GetProperty("OBJECTID").GetInt64());
        Assert.Equal("esriGeometryPolygon", hit.GetProperty("geometryType").GetString());
        Assert.True(hit.GetProperty("geometry").TryGetProperty("rings", out _));

        var upper = await BodyAsync(await client.GetAsync($"{Root}/findsvc/ImageServer/find?f=json&searchText=SECOND"));
        Assert.Equal("second", Assert.Single(upper.GetProperty("results").EnumerateArray()).GetProperty("value").GetString());

        var scoped = await BodyAsync(await client.GetAsync(
            $"{Root}/findsvc/ImageServer/find?f=json&searchText=7&searchFields=Name&returnGeometry=false"));
        Assert.Empty(scoped.GetProperty("results").EnumerateArray());
        var noGeometry = await BodyAsync(await client.GetAsync($"{Root}/findsvc/ImageServer/find?f=json&searchText=first&returnGeometry=false"));
        Assert.False(Assert.Single(noGeometry.GetProperty("results").EnumerateArray()).TryGetProperty("geometry", out _));
    }

    [Fact]
    public async Task Find_requires_a_catalog_and_a_search_text()
    {
        await using var catalog = new MissingFactory(_directory, _rasterPath, "raster.find", catalog: true, statistics: true, attributeTable: false);
        var catalogClient = await ImageServiceAsync(catalog, "findsvc", "raster.find");
        await using var plain = new MissingFactory(_directory, _rasterPath, "raster.plain", catalog: false, statistics: true, attributeTable: false);
        var plainClient = await ImageServiceAsync(plain, "plainsvc", "raster.plain");

        Assert.Equal(HttpStatusCode.BadRequest, (await plainClient.GetAsync($"{Root}/plainsvc/ImageServer/find?f=json&searchText=first")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await catalogClient.GetAsync($"{Root}/findsvc/ImageServer/find?f=json")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await catalogClient.GetAsync($"{Root}/findsvc/ImageServer/find?f=json&searchText=x&fromGeometry=%7B%22x%22%3A1%2C%22y%22%3A2%7D")).StatusCode);
    }

    [Fact]
    public async Task Statistics_returns_the_stored_band_statistics()
    {
        await using var factory = new MissingFactory(_directory, _rasterPath, "raster.stats", catalog: false, statistics: true, attributeTable: false);
        var client = await ImageServiceAsync(factory, "stats", "raster.stats");

        var body = await BodyAsync(await client.GetAsync($"{Root}/stats/ImageServer/statistics?f=json"));

        var band = Assert.Single(body.GetProperty("statistics").EnumerateArray());
        Assert.Equal(0, band.GetProperty("min").GetDouble());
        Assert.Equal(255, band.GetProperty("max").GetDouble());
        Assert.Equal(82.707, band.GetProperty("mean").GetDouble(), 3);
        Assert.Equal(39.838, band.GetProperty("standardDeviation").GetDouble(), 3);
        Assert.Equal(1, band.GetProperty("skipX").GetInt32());
        Assert.Equal(1, band.GetProperty("skipY").GetInt32());
    }

    [Fact]
    public async Task Statistics_without_stored_stats_is_not_found()
    {
        await using var factory = new MissingFactory(_directory, _rasterPath, "raster.nostats", catalog: false, statistics: false, attributeTable: false);
        var client = await ImageServiceAsync(factory, "nostats", "raster.nostats");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Root}/nostats/ImageServer/statistics?f=json")).StatusCode);
    }

    [Fact]
    public async Task Compute_histograms_returns_per_band_counts()
    {
        await using var factory = new MissingFactory(_directory, _rasterPath, "raster.hist", catalog: false, statistics: true, attributeTable: false);
        var client = await ImageServiceAsync(factory, "hist", "raster.hist");

        var full = await BodyAsync(await client.GetAsync(
            $"{Root}/hist/ImageServer/computeHistograms?f=json&geometryType=esriGeometryEnvelope&geometry=" +
            Uri.EscapeDataString("{\"xmin\":0,\"ymin\":0,\"xmax\":8,\"ymax\":6,\"spatialReference\":{\"wkid\":4326}}")));
        var band = Assert.Single(full.GetProperty("histograms").EnumerateArray());
        Assert.Equal(256, band.GetProperty("size").GetInt32());
        Assert.Equal(-0.5, band.GetProperty("min").GetDouble());
        Assert.Equal(255.5, band.GetProperty("max").GetDouble());
        var counts = band.GetProperty("counts").EnumerateArray().Select(value => value.GetInt64()).ToArray();
        Assert.Equal(256, counts.Length);
        Assert.Equal(Width * Height, counts.Sum());
        Assert.Equal(1, counts[0]);
        Assert.Equal(1, counts[8]);
        Assert.Equal(1, counts[16]);
        Assert.Equal(1, counts[47]);
        Assert.Equal(0, counts[48]);

        var part = await BodyAsync(await client.GetAsync(
            $"{Root}/hist/ImageServer/computeHistograms?f=json&geometryType=esriGeometryEnvelope&geometry=" +
            Uri.EscapeDataString("{\"xmin\":0,\"ymin\":0,\"xmax\":4,\"ymax\":3,\"spatialReference\":{\"wkid\":4326}}")));
        var partCounts = Assert.Single(part.GetProperty("histograms").EnumerateArray())
            .GetProperty("counts").EnumerateArray().Select(value => value.GetInt64()).ToArray();
        Assert.Equal(12, partCounts.Sum());
    }

    [Fact]
    public async Task Compute_histograms_validates_its_inputs()
    {
        await using var factory = new MissingFactory(_directory, _rasterPath, "raster.hist", catalog: false, statistics: true, attributeTable: false);
        var client = await ImageServiceAsync(factory, "hist", "raster.hist");
        var geometry = Uri.EscapeDataString("{\"xmin\":0,\"ymin\":0,\"xmax\":8,\"ymax\":6,\"spatialReference\":{\"wkid\":4326}}");

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync($"{Root}/hist/ImageServer/computeHistograms?f=json&geometryType=esriGeometryEnvelope")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync($"{Root}/hist/ImageServer/computeHistograms?f=json&geometryType=esriGeometryPoint&geometry={geometry}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync($"{Root}/hist/ImageServer/computeHistograms?f=json&geometryType=esriGeometryEnvelope&geometry={geometry}&renderingRule=%7B%7D")).StatusCode);
        var outside = Uri.EscapeDataString("{\"xmin\":100,\"ymin\":100,\"xmax\":104,\"ymax\":106,\"spatialReference\":{\"wkid\":4326}}");
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.GetAsync($"{Root}/hist/ImageServer/computeHistograms?f=json&geometryType=esriGeometryEnvelope&geometry={outside}")).StatusCode);
    }

    [Fact]
    public async Task Raster_attribute_table_returns_the_configured_classes()
    {
        await using var factory = new MissingFactory(_directory, _rasterPath, "raster.rat", catalog: false, statistics: true, attributeTable: true);
        var client = await ImageServiceAsync(factory, "ratsvc", "raster.rat");

        var table = await BodyAsync(await client.GetAsync($"{Root}/ratsvc/ImageServer/rasterAttributeTable?f=json"));

        Assert.Equal("OBJECTID", table.GetProperty("objectIdFieldName").GetString());
        var fields = table.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(["OID", "Value", "Count", "ClassName"], fields.Select(field => field.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(
            ["esriFieldTypeOID", "esriFieldTypeInteger", "esriFieldTypeDouble", "esriFieldTypeString"],
            fields.Select(field => field.GetProperty("type").GetString()!).ToArray());
        Assert.Equal(50, fields[3].GetProperty("length").GetInt32());
        Assert.False(fields[0].TryGetProperty("length", out _));
        var first = table.GetProperty("features").EnumerateArray().Select(feature => feature.GetProperty("attributes")).ToArray();
        Assert.Equal(2, first.Length);
        Assert.Equal(0, first[0].GetProperty("OID").GetInt64());
        Assert.Equal(0, first[0].GetProperty("Value").GetInt64());
        Assert.Equal(40, first[0].GetProperty("Count").GetDouble());
        Assert.Equal("Background", first[0].GetProperty("ClassName").GetString());
        Assert.Equal("Bright", first[1].GetProperty("ClassName").GetString());
    }

    [Fact]
    public async Task Raster_attribute_table_without_a_table_is_not_found()
    {
        await using var factory = new MissingFactory(_directory, _rasterPath, "raster.norat", catalog: false, statistics: true, attributeTable: false);
        var client = await ImageServiceAsync(factory, "norat", "raster.norat");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Root}/norat/ImageServer/rasterAttributeTable?f=json")).StatusCode);
    }

    [Fact]
    public async Task Service_thumbnail_streams_the_dataset_image()
    {
        await using var factory = new MissingFactory(_directory, _rasterPath, "raster.thumb", catalog: false, statistics: true, attributeTable: false);
        var client = await ImageServiceAsync(factory, "thumbsvc", "raster.thumb");

        var bytes = await client.GetAsync($"{Root}/thumbsvc/ImageServer/thumbnail");
        Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
        Assert.Equal("image/png", bytes.Content.Headers.ContentType?.MediaType);
        using var image = Image.NewFromBuffer(await bytes.Content.ReadAsByteArrayAsync());
        Assert.Equal(Width, image.Width);
        Assert.Equal(Height, image.Height);

        var href = await BodyAsync(await client.GetAsync($"{Root}/thumbsvc/ImageServer/thumbnail?f=json"));
        Assert.Equal(Width, href.GetProperty("width").GetInt32());
        Assert.Equal(Height, href.GetProperty("height").GetInt32());
        Assert.Contains("f=image", href.GetProperty("href").GetString());
    }

    [Fact]
    public async Task Metadata_without_authored_xml_is_not_found()
    {
        // T-056: the service-level metadata is the map's authored XML
        // document (ADR-0068), not a JSON dataset projection. Without
        // authoring the resource is a typed not.found, like the stored
        // statistics and the raster attribute table.
        await using var factory = new MissingFactory(_directory, _rasterPath, "raster.meta", catalog: false, statistics: true, attributeTable: false);
        var client = await ImageServiceAsync(factory, "metasvc", "raster.meta");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Root}/metasvc/ImageServer/metadata")).StatusCode);
    }

    [Fact]
    public async Task Info_omits_statistics_the_item_does_not_store()
    {
        // Catalog items carry their own file metadata; the dataset's stored
        // statistics surface on the statistics resource, not per-item info.
        // The info statistics member itself is pinned by adapter unit tests.
        await using var stored = new MissingFactory(_directory, _rasterPath, "raster.infostats", catalog: true, statistics: true, attributeTable: false);
        var storedClient = await ImageServiceAsync(stored, "infostats", "raster.infostats");

        var info = await BodyAsync(await storedClient.GetAsync($"{Root}/infostats/ImageServer/7/info?f=json"));

        Assert.False(info.TryGetProperty("statistics", out _));
        Assert.Equal("U8", info.GetProperty("pixelType").GetString());
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

    private sealed class MissingFactory(
        string directory, string rasterPath, string dataset, bool catalog, bool statistics, bool attributeTable)
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
            IReadOnlyList<RasterBandStatistics>? stats = statistics ? [new RasterBandStatistics(0, 255, 82.707, 39.838)] : null;
            var table = attributeTable
                ? new RasterAttributeTable(
                    "OBJECTID",
                    [new RasterAttributeField("OID", AttributeKind.Int64, Nullable: false),
                     new RasterAttributeField("Value", AttributeKind.Int64, Nullable: false),
                     new RasterAttributeField("Count", AttributeKind.Double, Nullable: false),
                     new RasterAttributeField("ClassName", AttributeKind.String, Length: 50)],
                    [[AttributeValue.FromInt64(0), AttributeValue.FromInt64(0), AttributeValue.FromDouble(40), AttributeValue.FromString("Background")],
                     [AttributeValue.FromInt64(1), AttributeValue.FromInt64(87), AttributeValue.FromDouble(8), AttributeValue.FromString("Bright")]])
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
