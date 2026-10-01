using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Spatial.Host.Tests;

/// <summary>
/// The workbench Parity page contract over a Census-shaped dataset (the
/// <c>tests/fixtures/census/census-states.geojson</c> stand-in for the Esri
/// <c>Census</c> MapServer <c>states</c> layer): the exact localhost URL
/// shapes the page builds — <c>MapServer/export?f=image</c>,
/// <c>FeatureServer/0/query</c> count-only and sample with the parity bbox
/// envelope (<c>-125,25,-66,50</c>, WKID 4326) — return real data instead of
/// errors, so the localhost panels can stand beside the public Esri ones.
/// </summary>
/// <remarks>
/// One host for the class, one dataset and one map name per test (ADR-0160,
/// ADR-0161). The census fixture is ingested into the shared memory store, so
/// the dataset id — not the map name — is what keeps one test's ingest out of
/// another's.
/// </remarks>
public sealed class ParityCensusTests : IClassFixture<ParityCensusTests.ParityHost>
{
    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";

    /// <summary>The Parity page defaults both panels share (see <c>parity.ts</c>).</summary>
    private const string Bbox = "-125,25,-66,50";
    private const string Sr = "4326";

    private const string FillStyle =
        """[{"type":"fill","layout":{"visibility":"visible"},"paint":{"fill-color":"#3d7ea6","fill-opacity":0.35,"fill-outline-color":"#3d7ea6"}}]""";

    private static readonly string[] FeatureAndMapServices = ["feature", "map"];

    private readonly ParityHost _host;

    public ParityCensusTests(ParityHost host) => _host = host;

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "eng", "verify.sh")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate the repository root (eng/verify.sh) from the test output.");
        return directory.FullName;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>
    /// Ingests the census fixture under a dataset id this test owns and
    /// publishes it as a map this test owns, exposing both servers.
    /// </summary>
    private async Task<(HttpClient Client, string Service)> CensusServiceAsync()
    {
        var client = _host.Client;
        var dataset = _host.NextDatasetName("census.states");
        var service = _host.NextMapName("census");
        var geojson = await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "tests", "fixtures", "census", "census-states.geojson"));

        var ingest = await client.SendAsync(Authorized(
            HttpMethod.Post,
            $"/api/ingest?store=memory&dataset={dataset}&srid=4326&format=geojson",
            new StringContent(geojson, Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);
        Assert.Equal(2, (await BodyAsync(ingest)).GetProperty("features").GetInt64());

        var map = JsonSerializer.Serialize(new
        {
            name = service,
            store = "memory",
            services = FeatureAndMapServices,
            layers = new[] { new { dataset, layerId = 0, name = "States", style = FillStyle } },
        });
        var publish = await client.SendAsync(Authorized(
            HttpMethod.Put, $"/api/maps/{service}", new StringContent(map, Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        return (client, service);
    }

    [Fact]
    public async Task The_parity_map_export_renders_census_bytes()
    {
        var (client, service) = await CensusServiceAsync();

        // The exact MapServer/export shape parity.ts builds (f=image bytes).
        var export = await client.GetAsync(
            $"{Root}/{service}/MapServer/export?bbox={Bbox}&bboxSR={Sr}&imageSR={Sr}&size=800,600&format=png&f=image");

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("image/png", export.Content.Headers.ContentType?.MediaType);
        Assert.True((await export.Content.ReadAsByteArrayAsync()).Length > 64);
    }

    [Fact]
    public async Task The_parity_feature_count_covers_both_states()
    {
        var (client, service) = await CensusServiceAsync();

        // The exact count-only shape parity.ts builds for the bbox envelope.
        var query = $"where={Uri.EscapeDataString("1=1")}&returnCountOnly=true"
            + $"&geometry={Uri.EscapeDataString(Bbox)}&geometryType=esriGeometryEnvelope"
            + $"&spatialRel=esriSpatialRelIntersects&inSR={Sr}&f=json";
        var count = await BodyAsync(await client.GetAsync($"{Root}/{service}/FeatureServer/0/query?{query}"));

        Assert.Equal(2, count.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task The_parity_feature_sample_returns_the_state_attributes()
    {
        var (client, service) = await CensusServiceAsync();

        // The exact sample shape parity.ts builds: no geometry, capped page.
        var query = $"where={Uri.EscapeDataString("1=1")}&outFields=*&returnGeometry=false&resultRecordCount=20"
            + $"&geometry={Uri.EscapeDataString(Bbox)}&geometryType=esriGeometryEnvelope"
            + $"&spatialRel=esriSpatialRelIntersects&inSR={Sr}&f=json";
        var sample = await BodyAsync(await client.GetAsync($"{Root}/{service}/FeatureServer/0/query?{query}"));

        var names = sample.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("NAME").GetString() ?? string.Empty)
            .Order()
            .ToArray();
        Assert.Equal(["California", "Texas"], names);
    }

    /// <summary>
    /// One host for the class, with an admin token and a per-class map file
    /// (ADR-0160). The dataset and the map name are the test's.
    /// </summary>
    public sealed class ParityHost : ClassHostFixture
    {
        public ParityHost()
            : base("spatial-parity")
        {
        }

        protected override void ConfigureHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", MapsPath);
        }
    }
}
