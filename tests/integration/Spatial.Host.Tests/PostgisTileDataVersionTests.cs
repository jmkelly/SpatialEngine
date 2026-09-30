using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Spatial.Client;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Host.Tests;

/// <summary>
/// The data version in the tile cache key, for a <em>durable</em> store
/// (ADR-0083, ADR-0129): a PostGIS-backed dataset's content version is a fact
/// the database holds, so a write moves the tile cache key on the host that
/// made it <em>and</em> on a second host reading the same database. A rendered
/// tile therefore re-renders after a write with no
/// <c>DELETE /api/render/cache</c> anywhere.
/// </summary>
public sealed class PostgisTileDataVersionTests : IAsyncLifetime
{
    private const string Circle =
        """[{"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#ff0000","circle-radius":20,"circle-opacity":1.0}}]""";

    private const int TokenLength = 64;

    private static readonly string[] TileServices = ["tiles"];

    private readonly List<string> _created = [];

    private HostScope? _scope;

    private HttpClient Http => _scope!.Http;

    private SpatialClient Client => _scope!.Client;

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

        foreach (var dataset in _created)
        {
            await PostgisTestDatabase.ExecuteAsync($"DROP TABLE IF EXISTS {Qualified(dataset)} CASCADE");
        }

        await _scope.DisposeAsync();
    }

    [SkippableFact]
    public async Task A_write_to_a_postgis_dataset_misses_the_tile_cache()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");
        var dataset = await CreateAsync("tiled");
        await PutMapAsync(Http, "postgisversioned", dataset, Circle);
        using var first = await Http.GetAsync(Tile("postgisversioned"));
        Assert.Equal("false", Cached(first));
        var before = await first.Content.ReadAsByteArrayAsync();

        using var warm = await Http.GetAsync(Tile("postgisversioned"));
        Assert.Equal("true", Cached(warm));
        Assert.Equal(before, await warm.Content.ReadAsByteArrayAsync());

        await Client.WriteAsync(dataset, Places());

        using var third = await Http.GetAsync(Tile("postgisversioned"));

        Assert.Equal("false", Cached(third));
        Assert.NotEqual(before, await third.Content.ReadAsByteArrayAsync());
    }

    [SkippableFact]
    public async Task A_write_to_another_postgis_dataset_leaves_this_maps_tile_cached()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");
        var watched = await CreateAsync("tiledwatched");
        var other = await CreateAsync("tiledother");
        await PutMapAsync(Http, "postgiswatched", watched, Circle);
        await PutMapAsync(Http, "postgisother", other, Circle);
        using var warm = await Http.GetAsync(Tile("postgisother"));
        Assert.Equal("false", Cached(warm));

        await Client.WriteAsync(watched, Places());

        using var response = await Http.GetAsync(Tile("postgisother"));
        Assert.Equal("true", Cached(response));
    }

    [SkippableFact]
    public async Task A_write_from_a_second_host_is_visible_to_the_first()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");
        var dataset = await CreateAsync("tiledshared");
        await PutMapAsync(Http, "postgisshared", dataset, Circle);
        using var warm = await Http.GetAsync(Tile("postgisshared"));
        Assert.Equal("false", Cached(warm));
        using var hit = await Http.GetAsync(Tile("postgisshared"));
        Assert.Equal("true", Cached(hit));
        var before = hit.Headers.GetValues("X-Tile-Version").Single();

        // A second host, its own DI singletons and its own connection pool,
        // writing the same durable dataset.
        await using var second = HostScope.Start();
        await second.Client.WriteAsync(dataset, Places());

        using var after = await Http.GetAsync(Tile("postgisshared"));

        Assert.Equal("false", Cached(after));
        Assert.NotEqual(before, after.Headers.GetValues("X-Tile-Version").Single());
    }

    [SkippableFact]
    public async Task A_postgis_tile_reports_the_content_version_it_was_rendered_at()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");
        var dataset = await CreateAsync("tiledreported");
        await PutMapAsync(Http, "postgisreported", dataset, Circle);

        using var first = await Http.GetAsync(Tile("postgisreported"));
        var before = first.Headers.GetValues("X-Tile-Version").Single();
        Assert.Equal(TokenLength, before.Length);

        await Client.WriteAsync(dataset, Places());

        using var second = await Http.GetAsync(Tile("postgisreported"));
        Assert.NotEqual(before, second.Headers.GetValues("X-Tile-Version").Single());
    }

    private static string Tile(string map) => $"/api/maps/{map}/tiles/0/0/0.png";

    private static string Cached(HttpResponseMessage response) => response.Headers.GetValues("X-Tile-Cached").Single();

    /// <summary>Creates a PostGIS dataset in the container and registers it for teardown.</summary>
    private async Task<string> CreateAsync(string suffix)
    {
        var dataset = $"public.host_{suffix}";
        _created.Add(dataset);
        return await Client.CreateDatasetAsync(dataset, Places(), 4326);
    }

    private static async Task PutMapAsync(HttpClient http, string name, string dataset, string? style)
    {
        var body = JsonSerializer.Serialize(new
        {
            name,
            store = "postgis",
            services = TileServices,
            layers = new[] { new { dataset, layerId = 0, name = "places", style } },
        });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/maps/{name}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", HostScope.Token);
        using var response = await http.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Qualified(string dataset)
    {
        var parts = dataset.Split('.');
        return $"\"{parts[0]}\".\"{parts[1]}\"";
    }

    private static FeatureBatch Places() => Batch((1, "Berlin", 13.405, 52.52), (2, "Perth", 115.86, -31.95));

    private static FeatureBatch Batch(params (long Id, string Name, double X, double Y)[] rows)
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64, false),
            new FieldDefinition("name", AttributeKind.String, true),
            new FieldDefinition("geom", AttributeKind.Geometry, true),
        ]);
        return new FeatureBatch(schema, rows.Select(row => new Feature(
            new FeatureId(row.Id.ToString(CultureInfo.InvariantCulture)),
            schema,
            [
                AttributeValue.FromInt64(row.Id),
                AttributeValue.FromString(row.Name),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(row.X, row.Y, CoordinateReference.Epsg(4326))),
            ])).ToArray());
    }

    /// <summary>
    /// The host under test plus its disposable surroundings: one
    /// admin-authorized client and a private map file. A second scope is a
    /// second host over the same database, which is what makes a durable
    /// version worth having.
    /// </summary>
    private sealed class HostScope : IAsyncDisposable
    {
        public const string Token = "postgis-tile-admin-token";

        private readonly WebApplicationFactory<Program> _factory;

        private readonly string _mapsDirectory;

        private HostScope(WebApplicationFactory<Program> factory, string mapsDirectory)
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
            var mapsDirectory = Directory.CreateTempSubdirectory("spatial-postgis-tiles-").FullName;
            return new HostScope(new HostFactory(Path.Combine(mapsDirectory, "maps.json")), mapsDirectory);
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            Directory.Delete(_mapsDirectory, recursive: true);
        }
    }

    /// <summary>A host with the containerised PostGIS and an admin token for the write routes.</summary>
    private sealed class HostFactory(string mapsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Spatial:Postgis:ConnectionString", PostgisTestDatabase.ConnectionString ?? string.Empty);
            builder.UseSetting("Spatial:Admin:Token", HostScope.Token);
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
        }
    }
}
