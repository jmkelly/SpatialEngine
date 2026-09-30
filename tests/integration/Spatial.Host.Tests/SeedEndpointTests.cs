using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Contracts.Providers;
using Spatial.Host.Api;

namespace Spatial.Host.Tests;

/// <summary>
/// The development seed endpoint (ADR-0078): a manifest POST that downloads,
/// ingests and publishes without restarting the host. Mounted only in
/// Development; token-gated when a token is configured, open otherwise.
/// </summary>
public sealed class SeedEndpointTests : IDisposable
{
    private const string Token = "test-admin-token";

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-seed-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private const string CitiesJson = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[13.4,52.5]},"properties":{"name":"Berlin"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[2.35,48.85]},"properties":{"name":"Paris"}}
        ]}
        """;

    private const string RiversJson = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"LineString","coordinates":[[0,0],[1,1]]},"properties":{"name":"Rill"}}
        ]}
        """;

    private sealed class StubFetcher(Dictionary<string, string> files) : ISeedSourceFetcher
    {
        public Task<byte[]> FetchAsync(string url, long maxBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!files.TryGetValue(url, out var body))
            {
                throw SpatialException.Missing($"Download failed (404) for {url}.");
            }

            return Task.FromResult(Encoding.UTF8.GetBytes(body));
        }
    }

    private SeedFactory Factory(
        Dictionary<string, string>? files = null, string? token = null, string? environment = null) =>
        new SeedFactory(
            Path.Combine(_directory, $"maps-{Guid.NewGuid():N}.json"),
            files ?? new Dictionary<string, string>
            {
                ["https://example.test/cities.geojson"] = CitiesJson,
                ["https://example.test/rivers.geojson"] = RiversJson,
            },
            token,
            environment);

    private static readonly string[] FeatureService = ["feature"];

    private static object SeedDocument(bool force = false) => new
    {
        store = "memory",
        force,
        sources = new[]
        {
            new { id = "public.cities", url = "https://example.test/cities.geojson", format = "geojson", srid = 4326, identity = "auto" },
            new { id = "public.rivers", url = "https://example.test/rivers.geojson", format = "geojson", srid = 4326, identity = "auto" },
        },
        maps = new[]
        {
            new
            {
                name = "SeedCities",
                services = FeatureService,
                layers = new[]
                {
                    new { dataset = "public.cities", name = "Cities", geometry = "point", style = new { color = "#ffd54f", opacity = 0.9, lineWidth = 2, radius = 3, visible = true } },
                },
            },
            new
            {
                name = "SeedRivers",
                services = FeatureService,
                layers = new[]
                {
                    new { dataset = "public.rivers", name = "Rivers", geometry = "line", style = new { color = "#1e88e5", opacity = 0.9, lineWidth = 2, radius = 5, visible = true } },
                },
            },
        },
    };

    [Fact]
    public async Task Seed_loads_sources_and_publishes_maps()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/seed", Json(JsonSerializer.Serialize(SeedDocument())));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal(2, body.GetProperty("ingested").GetInt32());
        Assert.Equal(0, body.GetProperty("reused").GetInt32());
        Assert.Equal(2, body.GetProperty("published").GetInt32());
        Assert.Empty(body.GetProperty("failures").EnumerateArray());

        var query = await client.GetAsync(
            "/arcgis/rest/services/SeedCities/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json");
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        Assert.Equal(2, (await BodyAsync(query)).GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Seed_reuses_datasets_on_repeat_runs_unless_forced()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        await client.PostAsync("/api/seed", Json(JsonSerializer.Serialize(SeedDocument())));

        var reused = await BodyAsync(await client.PostAsync(
            "/api/seed", Json(JsonSerializer.Serialize(SeedDocument()))));
        Assert.Equal(0, reused.GetProperty("ingested").GetInt32());
        Assert.Equal(2, reused.GetProperty("reused").GetInt32());

        var forced = await BodyAsync(await client.PostAsync(
            "/api/seed", Json(JsonSerializer.Serialize(SeedDocument(force: true)))));
        // Forcing re-attempts the ingest, which the store rejects per item
        // because it still holds the dataset — the same semantics as
        // `seed.mjs --force` (ADR-0078).
        Assert.Equal(0, forced.GetProperty("ingested").GetInt32());
        var forceFailures = forced.GetProperty("failures").EnumerateArray().ToArray();
        Assert.Equal(2, forceFailures.Length);
        Assert.All(forceFailures, failure =>
            Assert.Equal("invalid.arguments", failure.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task Seed_records_per_item_failures_without_failing_the_run()
    {
        using var factory = Factory(new Dictionary<string, string>
        {
            ["https://example.test/cities.geojson"] = CitiesJson,
        });
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/seed", Json(JsonSerializer.Serialize(SeedDocument())));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal(1, body.GetProperty("ingested").GetInt32());
        var failures = body.GetProperty("failures").EnumerateArray().ToArray();
        Assert.Contains(failures, failure =>
            failure.GetProperty("target").GetString() == "public.rivers");
    }

    [Fact]
    public async Task Seed_is_not_mounted_outside_development()
    {
        using var factory = Factory(environment: "Production");
        var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/seed", Json(JsonSerializer.Serialize(SeedDocument())));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Seed_requires_the_admin_token_when_configured()
    {
        using var factory = Factory(token: Token);
        var client = factory.CreateClient();

        var missing = await client.PostAsync(
            "/api/seed", Json(JsonSerializer.Serialize(SeedDocument())));
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/seed")
        {
            Content = Json(JsonSerializer.Serialize(SeedDocument())),
        };
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        Assert.Equal(
            HttpStatusCode.Forbidden, (await client.SendAsync(wrong)).StatusCode);

        var authorized = new HttpRequestMessage(HttpMethod.Post, "/api/seed")
        {
            Content = Json(JsonSerializer.Serialize(SeedDocument())),
        };
        authorized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(authorized)).StatusCode);
    }

    [Fact]
    public async Task Seed_records_a_map_with_no_layers_as_an_item_failure()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var document = JsonSerializer.Serialize(new
        {
            store = "memory",
            sources = Array.Empty<object>(),
            maps = new[] { new { name = "Empty", services = FeatureService, layers = Array.Empty<object>() } },
        });

        var response = await client.PostAsync("/api/seed", Json(document));

        // Per-item failures never fail the run (ADR-0078): 200 with a failure
        // entry, mirroring the seed tool's summary.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal(0, body.GetProperty("published").GetInt32());
        var failures = body.GetProperty("failures").EnumerateArray().ToArray();
        var failure = Assert.Single(failures);
        Assert.Equal("Empty", failure.GetProperty("target").GetString());
        Assert.Equal("invalid.arguments", failure.GetProperty("code").GetString());
    }

    [Fact]
    public async Task SeedRunner_honours_cancellation()
    {
        using var factory = Factory();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var runner = new SeedRunner(
            services.GetRequiredService<ISeedSourceFetcher>(),
            services.GetRequiredService<IStoreRegistry>(),
            services.GetRequiredService<ICoordinateTransforms>(),
            services.GetRequiredService<IMapRegistry>(),
            new IngestOptions());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(
                new SeedRequest([], [], Store: "memory"),
                cancelled.Token));
    }

    private sealed class SeedFactory(
        string mapsPath, Dictionary<string, string> files, string? token, string? environment)
        : SpatialHostFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            if (environment is not null)
            {
                builder.UseEnvironment(environment);
            }

            if (token is not null)
            {
                builder.UseSetting("Spatial:Admin:Token", token);
            }

            builder.UseSetting("Spatial:Maps:Path", mapsPath);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ISeedSourceFetcher>(new StubFetcher(files));
            });
        }
    }
}
