using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// The QGIS WMS interop harness (T-013): every captured QGIS 4.2.2 request
/// in <c>tests/fixtures/qgis/qgis-4.2.2-wms.json</c> is replayed verbatim
/// against the OGC routes and must answer 200 with a valid body — never an
/// OGC service-exception document. The fixture setup mirrors the live
/// capture exactly (demo points plus ingested memory line/polygon datasets
/// under a <c>qgis</c> map), so a green suite means the next real QGIS
/// round sends only traffic proven here.
/// </summary>
public sealed class QgisReplayTests : IDisposable
{
    private const string Token = "test-admin-token";
    private const string Map = "qgis";

    private static readonly string[] GeometryKinds = ["point", "line", "polygon"];
    private static readonly string[] WmsService = ["wms"];

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "qgis-fixtures", "qgis-4.2.2-wms.json");

    private static readonly JsonElement Catalog =
        JsonDocument.Parse(File.ReadAllText(FixturePath)).RootElement;

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-qgis-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    public static IEnumerable<object[]> RequestNames() =>
        Catalog.GetProperty("requests").EnumerateArray()
            .Select(request => new object[] { request.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(RequestNames))]
    public async Task Qgis_request_replays_verbatim(string name)
    {
        using var context = await QgisContext.CreateAsync(_directory);
        var request = Catalog.GetProperty("requests").EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == name);

        var response = await context.Client.GetAsync($"/ogc/{Map}/wms?{request.GetProperty("query").GetString()}");
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(request.GetProperty("expectStatus").GetInt32(), (int)response.StatusCode);
        Assert.Equal(
            request.GetProperty("expectMediaType").GetString(),
            response.Content.Headers.ContentType?.MediaType);
        if (request.TryGetProperty("expectPngMagic", out _))
        {
            Assert.True(body.Length > 100, $"PNG body for '{name}' is suspiciously small ({body.Length} bytes).");
            Assert.Equal([0x89, 0x50, 0x4E, 0x47], body[..4]);
        }
        else
        {
            var text = Encoding.UTF8.GetString(body);
            foreach (var contained in request.GetProperty("expectBodyContains").EnumerateArray())
            {
                Assert.Contains(contained.GetString()!, text);
            }
        }

        foreach (var absent in request.GetProperty("expectBodyAbsent").EnumerateArray())
        {
            Assert.DoesNotContain(absent.GetString()!, Encoding.UTF8.GetString(body));
        }
    }

    [Fact]
    public async Task The_capture_covers_capabilities_maps_in_both_crs_and_identify_on_all_geometries()
    {
        var names = Catalog.GetProperty("requests").EnumerateArray()
            .Select(request => request.GetProperty("name").GetString()).ToArray();
        Assert.Contains("add-layer-getcapabilities", names);
        Assert.Contains("getmap-full-extent-4326", names);
        Assert.Contains("getmap-zoom-4326", names);
        Assert.Contains("getmap-3857", names);
        foreach (var kind in GeometryKinds)
        {
            Assert.Contains($"identify-{kind}-gml", names);
            Assert.Contains($"identify-{kind}-html", names);
        }

        Assert.Equal("4.2.2-Belém do Pará", Catalog.GetProperty("capture").GetProperty("qgisVersion").GetString());
    }

    /// <summary>A test host seeded exactly like the live capture: the demo
    /// points plus the fixture's line/polygon datasets ingested into the
    /// memory store, published as the <c>qgis</c> WMS map.</summary>
    private sealed class QgisContext : IDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;

        private QgisContext(WebApplicationFactory<Program> factory, HttpClient client)
        {
            _factory = factory;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<QgisContext> CreateAsync(string directory)
        {
            var factory = new QgisFactory(Path.Combine(directory, "maps.json"));
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

            foreach (var dataset in Catalog.GetProperty("seedDatasets").EnumerateArray())
            {
                var query =
                    $"store={dataset.GetProperty("store").GetString()}" +
                    $"&dataset={dataset.GetProperty("dataset").GetString()}" +
                    $"&srid={dataset.GetProperty("srid").GetInt32()}" +
                    $"&format={dataset.GetProperty("format").GetString()}";
                using var content = new StringContent(
                    dataset.GetProperty("body").GetString()!, Encoding.UTF8, "application/geo+json");
                var ingested = await client.PostAsync($"/api/ingest?{query}", content);
                Assert.Equal(HttpStatusCode.OK, ingested.StatusCode);
            }

            var layers = Catalog.GetProperty("mapLayers").EnumerateArray().Select((layer, index) =>
            {
                var record = new Dictionary<string, object?>
                {
                    ["dataset"] = layer.GetProperty("dataset").GetString(),
                    ["layerId"] = -1,
                    ["name"] = layer.GetProperty("name").GetString(),
                    ["style"] = layer.GetProperty("style").GetString(),
                };
                if (layer.TryGetProperty("store", out var store))
                {
                    record["store"] = store.GetString();
                }

                return record;
            }).ToArray();
            var map = JsonSerializer.Serialize(new { name = Map, store = "demo", services = WmsService, layers });
            using var mapContent = new StringContent(map, Encoding.UTF8, "application/json");
            var published = await client.PutAsync($"/api/maps/{Map}", mapContent);
            Assert.Equal(HttpStatusCode.OK, published.StatusCode);

            return new QgisContext(factory, client);
        }

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
        }
    }

    /// <summary>A host with an admin token and a per-test map file.</summary>
    private sealed class QgisFactory(string mapsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
        }
    }
}
