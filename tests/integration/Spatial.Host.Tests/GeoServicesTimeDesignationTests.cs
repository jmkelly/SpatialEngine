using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Spatial.Client;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Host.Tests;

/// <summary>
/// A served map layer carries its own designation of the dates bounding a
/// feature (ADR-0175, ADR-0182, ADR-0183): the publication declares
/// <c>timeFields</c> on the layer, the root advertises
/// <c>supportsTimeRelation</c> exactly when a served layer designates one, and
/// <c>contains</c>/<c>within</c> are served against that layer's extents rather
/// than refused. A layer that designates none keeps ADR-0100's flag and its
/// typed rejects.
///
/// <para>The layer is a real PostGIS table with <c>timestamptz</c> columns —
/// the only place the engine discovers date-typed fields — so the designation
/// is proved over the discovery path rather than over a hand-built schema.
/// </para>
/// </summary>
public sealed class GeoServicesTimeDesignationTests : IAsyncLifetime
{
    private const string Dataset = "public.host_timed_events";
    private const string Token = "designation-admin-token";

    private HostScope? _scope;

    private HttpClient Http => _scope!.Http;

    public Task InitializeAsync()
    {
        if (PostgisTestDatabase.Available)
        {
            _scope = HostScope.Start();
        }

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_scope is null)
        {
            return;
        }

        await PostgisTestDatabase.ExecuteAsync($"DROP TABLE IF EXISTS \"public\".\"host_timed_events\" CASCADE");
        await _scope.DisposeAsync();
    }

    /// <summary>
    /// Publishes <paramref name="service"/> over the dated table, designating
    /// the two date fields when <paramref name="designated"/>. Returns the
    /// response so a test can read the typed failure a bad declaration gives.
    /// </summary>
    private async Task<HttpResponseMessage> PublishAsync(string service, bool designated, object? timeFields = null)
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");
        var client = _scope!.Client;
        var dataset = await client.CreateDatasetAsync(Dataset, Events(), 4326);
        await client.WriteAsync(dataset, Events());
        var layer = new
        {
            dataset,
            layerId = 0,
            name = "events",
            timeFields = timeFields ?? (designated ? new { startField = "starts", endField = "ends" } : null),
        };
        var map = $$"""{"name":"{{service}}","store":"postgis","services":["map"],"layers":[{{JsonSerializer.Serialize(layer)}}]}""";

        return await Http.PutAsync($"/api/maps/{service}", new StringContent(map, Encoding.UTF8, "application/json"));
    }

    private async Task<JsonElement> RootAsync(string service)
    {
        await _scope!.ReadyAsync();
        var response = await Http.GetAsync($"/arcgis/rest/services/{service}/MapServer?f=json");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    // ---- the designation reaches the served layer ----

    [SkippableFact]
    public async Task A_designated_layer_advertises_the_time_relation_on_the_root()
    {
        Assert.Equal(HttpStatusCode.OK, (await PublishAsync("designated", designated: true)).StatusCode);

        var root = await RootAsync("designated");

        Assert.True(root.GetProperty("supportsTimeRelation").GetBoolean());
    }

    [SkippableFact]
    public async Task A_layer_that_designates_none_keeps_the_honest_flag_and_the_typed_refusals()
    {
        Assert.Equal(HttpStatusCode.OK, (await PublishAsync("undesignated", designated: false)).StatusCode);

        var root = await RootAsync("undesignated");
        Assert.False(root.GetProperty("supportsTimeRelation").GetBoolean());

        var response = await Http.GetAsync(Export("undesignated") + "&timeRelation=esriTimeRelationContains");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- the relations are served, not refused ----

    [SkippableFact]
    public async Task A_contains_export_is_served_on_a_designated_layer()
    {
        Assert.Equal(HttpStatusCode.OK, (await PublishAsync("contains", designated: true)).StatusCode);

        var response = await Http.GetAsync(Export("contains") + "&timeRelation=esriTimeRelationContains");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [SkippableFact]
    public async Task A_contains_identify_returns_the_feature_whose_extent_covers_the_window()
    {
        Assert.Equal(HttpStatusCode.OK, (await PublishAsync("identify", designated: true)).StatusCode);

        // The festival runs 2024-06-01..2024-06-10, so a window inside June
        // 2024 is contained by it, and the term covering 2020 is neither
        // contained by it nor containing it.
        Assert.Equal(
            ["festival"],
            await IdentifiedAsync("identify", Window(2024, 6, 5), "esriTimeRelationContains"));
        Assert.Empty(await IdentifiedAsync("identify", Window(2021, 1, 1), "esriTimeRelationContains"));
    }

    [SkippableFact]
    public async Task A_within_identify_returns_the_feature_whose_extent_is_covered_by_the_window()
    {
        Assert.Equal(HttpStatusCode.OK, (await PublishAsync("within", designated: true)).StatusCode);

        // The festival's extent is 2024-06-01..2024-06-10: a window covering
        // it exactly is within it, and one strictly inside it is neither
        // within it nor contained by it.
        Assert.Equal(["festival"], await IdentifiedAsync("within", $"{Window(2024, 6, 1)},{Window(2024, 6, 10)}", "esriTimeRelationWithin"));
        Assert.Empty(await IdentifiedAsync("within", $"{Window(2024, 6, 2)},{Window(2024, 6, 9)}", "esriTimeRelationWithin"));
    }

    [SkippableFact]
    public async Task An_undesignated_layer_still_answers_with_the_rule_it_always_had()
    {
        Assert.Equal(HttpStatusCode.OK, (await PublishAsync("bag", designated: false)).StatusCode);

        // Nothing is designated, so no row's extent is compared with the
        // window: the festival's own instants inside it pass it, exactly as
        // they did before a designation could be declared at all.
        Assert.Equal(["festival"], await IdentifiedAsync("bag", $"{Window(2024, 6, 1)},{Window(2024, 6, 10)}"));
    }

    /// <summary>The names the identify under a time window and relation returns.</summary>
    private async Task<string[]> IdentifiedAsync(string service, string time, string relation = "esriTimeRelationOverlaps")
    {
        await _scope!.ReadyAsync();
        var response = await Http.GetAsync($"{Identify(service)}&time={time}&timeRelation={relation}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("results")
            .EnumerateArray()
            .Select(result => result.GetProperty("value").GetString() ?? string.Empty)
            .ToArray();
    }

    // ---- a designation that cannot hold is rejected where it is declared ----

    [SkippableFact]
    public async Task A_designation_over_a_field_the_dataset_does_not_have_is_rejected_at_declaration()
    {
        var response = await PublishAsync(
            "wrongfield", designated: true, timeFields: new { startField = "begins", endField = "ends" });

        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("begins", text, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_designation_over_a_field_that_is_not_a_date_is_rejected_at_declaration()
    {
        var response = await PublishAsync(
            "wrongkind", designated: true, timeFields: new { startField = "name", endField = "ends" });

        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("name", text, StringComparison.Ordinal);
    }

    private static string Export(string service) =>
        $"/arcgis/rest/services/{service}/MapServer/export?f=image&bbox=10,50,16,54&bboxSR=4326&imageSR=4326"
        + "&size=100,100&format=png&layers=show:0";

    private static string Identify(string service) =>
        "/arcgis/rest/services/" + service + "/MapServer/identify?f=json&geometry="
        + Uri.EscapeDataString("""{"x":13.4,"y":52.5,"spatialReference":{"wkid":4326}}""")
        + "&geometryType=esriGeometryPoint&sr=4326&tolerance=5&layers=all"
        + "&mapExtent=" + Uri.EscapeDataString("10,50,16,54") + "&imageDisplay=" + Uri.EscapeDataString("400,400,96");

    private static string Window(int year, int month, int day) =>
        new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);

    /// <summary>Two features over dated columns: a June festival and a 2020 term.</summary>
    private static FeatureBatch Events()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("name", AttributeKind.String, true),
            new FieldDefinition("starts", AttributeKind.DateTimeOffset, true),
            new FieldDefinition("ends", AttributeKind.DateTimeOffset, true),
            new FieldDefinition("geom", AttributeKind.Geometry, true),
        ]);
        return new FeatureBatch(schema,
        [
            Feature(schema, 1, "festival", 2024, 6, 1, 2024, 6, 10, 13.4, 52.5),
            Feature(schema, 2, "term", 2020, 1, 1, 2020, 12, 31, 2.35, 48.85),
        ]);
    }

    private static Feature Feature(
        FeatureSchema schema, long id, string name,
        int startYear, int startMonth, int startDay, int endYear, int endMonth, int endDay,
        double x, double y) =>
        new(
            new FeatureId(id.ToString(CultureInfo.InvariantCulture)),
            schema,
            [
                AttributeValue.FromInt64(id),
                AttributeValue.FromString(name),
                AttributeValue.FromDateTimeOffset(new DateTimeOffset(startYear, startMonth, startDay, 0, 0, 0, TimeSpan.Zero)),
                AttributeValue.FromDateTimeOffset(new DateTimeOffset(endYear, endMonth, endDay, 0, 0, 0, TimeSpan.Zero)),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326))),
            ]);

    /// <summary>The host under test plus a client authenticated for the map write route.</summary>
    private sealed class HostScope : IAsyncDisposable
    {
        private readonly SpatialHostFactory _factory;

        private readonly string _mapsDirectory;

        private HostScope(SpatialHostFactory factory, string mapsDirectory)
        {
            _factory = factory;
            _mapsDirectory = mapsDirectory;
            Http = factory.CreateClient();
            Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            Client = new SpatialClient(Http);
        }

        public HttpClient Http { get; }

        public SpatialClient Client { get; }

        public static HostScope Start()
        {
            var mapsDirectory = Directory.CreateTempSubdirectory("spatial-designation-").FullName;
            var factory = new HostFactory(Path.Combine(mapsDirectory, "maps.json"));
            return new HostScope(factory, mapsDirectory);
        }

        /// <summary>
        /// Waits for the host to be answering: the first request into a fresh
        /// <see cref="WebApplicationFactory{TEntryPoint}"/> boots it.
        /// </summary>
        public async Task ReadyAsync()
        {
            using var response = await Http.GetAsync("/health/live");
            Assert.True(response.IsSuccessStatusCode, $"the host did not start: {await response.Content.ReadAsStringAsync()}");
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            Directory.Delete(_mapsDirectory, recursive: true);
        }
    }

    /// <summary>A host with the containerised PostGIS and an admin token for the map write route.</summary>
    private sealed class HostFactory(string mapsPath) : SpatialHostFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Spatial:Postgis:ConnectionString", PostgisTestDatabase.ConnectionString ?? string.Empty);
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
        }
    }
}