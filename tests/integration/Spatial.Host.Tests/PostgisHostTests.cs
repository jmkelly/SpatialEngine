using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Spatial.Client;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Host.Tests;

/// <summary>
/// The host's PostGIS store against a real PostGIS container (ADR-0028,
/// ADR-0033): the store is configured from the test container's connection
/// string, so the suite needs no operator-set
/// <c>SPATIAL_POSTGIS_CONNECTION</c>, and the typed host API plus the Esri
/// FeatureServer surface read and write the same database.
/// </summary>
public sealed class PostgisHostTests : IAsyncLifetime
{
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
    public async Task The_postgis_store_serves_the_container_database()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");
        await CreateAsync(Dataset("catalogue"));

        var ids = (await Client.ListCatalogueAsync("postgis")).Select(summary => summary.Id).ToArray();

        Assert.Contains(Dataset("catalogue"), ids);
    }

    [SkippableFact]
    public async Task A_created_dataset_is_described_with_its_geometry_and_srid()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");

        var description = await Client.DescribeDatasetAsync(await CreateAsync(Dataset("described")), "postgis");

        Assert.Equal(4326, description.Srid);
        Assert.Equal("geom", description.GeometryColumn);
        Assert.Contains(description.Schema.Fields, field => field.Name == "name");
    }

    [SkippableFact]
    public async Task Written_features_round_trip_through_scan_and_query()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");
        var dataset = await CreateAsync(Dataset("points"));

        Assert.Equal(2, await Client.WriteAsync(dataset, Places()));

        var scanned = await Client.ScanAsync(dataset, "postgis");
        Assert.Equal(["Berlin", "Paris"], Names(scanned));

        var inBox = await Client.QueryAsync(dataset, new BoundingBox(13.0, 52.0, 14.0, 53.0), store: "postgis");
        Assert.Equal(["Berlin"], Names(inBox));

        var filtered = await Client.QueryAsync(dataset, filter: "name = 'Paris'", store: "postgis");
        Assert.Equal(["Paris"], Names(filtered));
    }

    [SkippableFact]
    public async Task A_committed_transaction_keeps_its_features_and_a_rollback_drops_them()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");
        var dataset = await CreateAsync(Dataset("transacted"));

        var committed = await Client.BeginTransactionAsync();
        Assert.Equal(1, await Client.WriteAsync(dataset, BerlinOnly(), committed));
        Assert.True(await Client.CommitTransactionAsync(committed));

        var rolledBack = await Client.BeginTransactionAsync();
        Assert.Equal(1, await Client.WriteAsync(dataset, ParisOnly(), rolledBack));
        Assert.True(await Client.RollbackTransactionAsync(rolledBack));

        Assert.Equal(["Berlin"], Names(await Client.ScanAsync(dataset, "postgis")));
    }

    [SkippableFact]
    public async Task An_unknown_postgis_dataset_is_not_found()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");

        var exception = await Assert.ThrowsAsync<SpatialClientException>(
            () => Client.DescribeDatasetAsync("public.nowhere_at_all", "postgis"));

        Assert.Equal(404, exception.StatusCode);
        Assert.Equal("not.found", exception.Code);
    }

    [SkippableFact]
    public async Task A_cancelled_request_stops_the_postgis_scan()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");
        var dataset = await CreateAsync(Dataset("cancelled"));
        await Client.WriteAsync(dataset, Places());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client.ScanAsync(dataset, "postgis", cts.Token));
    }

    [SkippableFact]
    public async Task A_postgis_dataset_is_served_through_the_geoservices_feature_service()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");
        var dataset = await CreateAsync(Dataset("served"));
        await Client.WriteAsync(dataset, Places());
        var map = $$"""{"name":"postgisplaces","store":"postgis","services":["feature"],"layers":[{"dataset":"{{dataset}}","layerId":0}]}""";

        var published = await Http.PutAsync("/api/maps/postgisplaces", new StringContent(map, Encoding.UTF8, "application/json"));
        Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync());

        var response = await Http.GetAsync("/arcgis/rest/services/postgisplaces/FeatureServer/0/query?where=1%3D1&outFields=*&f=json");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var names = body.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("name").GetString() ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Berlin", "Paris"], names);
    }

    /// <summary>Creates a dataset in the container and registers it for teardown.</summary>
    private async Task<string> CreateAsync(string dataset)
    {
        _created.Add(dataset);
        return await Client.CreateDatasetAsync(dataset, Places(), 4326);
    }

    private static string[] Names(IReadOnlyList<FeatureBatch> batches) =>
        batches.SelectMany(batch => batch.Features)
            .Select(feature => feature["name"].StringValue)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string Dataset(string suffix) => $"public.host_{suffix}";

    private static string Qualified(string dataset)
    {
        var parts = dataset.Split('.');
        return $"\"{parts[0]}\".\"{parts[1]}\"";
    }

    private static FeatureBatch Places() => Batch((1, "Berlin", 13.405, 52.52), (2, "Paris", 2.3522, 48.8566));

    private static FeatureBatch BerlinOnly() => Batch((1, "Berlin", 13.405, 52.52));

    private static FeatureBatch ParisOnly() => Batch((2, "Paris", 2.3522, 48.8566));

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
    /// The host under test plus its disposable surroundings: one client
    /// (authenticated for the admin-authorized write routes) and the private
    /// map file the GeoServices test publishes into.
    /// </summary>
    private sealed class HostScope : IAsyncDisposable
    {
        public const string Token = "postgis-admin-token";

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
            var mapsDirectory = Directory.CreateTempSubdirectory("spatial-postgis-").FullName;
            var factory = new HostFactory(Path.Combine(mapsDirectory, "maps.json"));
            return new HostScope(factory, mapsDirectory);
        }

        public async ValueTask DisposeAsync()
        {
            await _factory.DisposeAsync();
            Directory.Delete(_mapsDirectory, recursive: true);
        }
    }

    /// <summary>A host with the containerised PostGIS and an admin token for the write routes.</summary>
    private sealed class HostFactory(string mapsPath) : SpatialHostFactory
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
