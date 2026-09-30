using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// The <c>hasZ</c>/<c>hasM</c> a FeatureServer layer root advertises
/// (SpatialEngine-fhf, ADR-0084). The flags are a description of the data, not
/// of the query surface, so they are derived from the coordinate layout the
/// store *declares* for the geometry column: a PostGIS column typed
/// <c>geometry(PointZ,4326)</c> is a type-system proof that every stored
/// coordinate carries a Z, while a column typed plain <c>geometry</c> proves
/// nothing and therefore advertises nothing. A two-dimensional dataset must
/// not claim Z, because ArcGIS clients branch on the flag and send Z in query
/// geometry and edit payloads when it is true.
/// </summary>
public sealed class GeoServicesLayerHasZTests : IAsyncLifetime, IDisposable
{
    private const string Token = "hasz-admin-token";
    private const string Root = "/arcgis/rest/services";
    private const string Service = "hasz";

    /// <summary>The second publication, map-only, that serves the same datasets through the MapServer surface.</summary>
    private const string MapService = "haszmap";

    private const string Survey = "public.hasz_survey";
    private const string Station = "public.hasz_station";
    private const string Track = "public.hasz_track";
    private const string Unconstrained = "public.hasz_unconstrained";

    private static readonly string[] FeatureAndMapService = ["feature", "map"];

    private static readonly string[] MapOnlyService = ["map"];

    private readonly List<string> _tables = [Survey, Station, Track, Unconstrained];

    private string? _directory;
    private WebApplicationFactory<Program>? _factory;

    public async Task InitializeAsync()
    {
        if (!PostgisTestDatabase.Available)
        {
            return;
        }

        _directory = Directory.CreateTempSubdirectory("spatial-hasz-").FullName;
        _factory = new HasZFactory(Path.Combine(_directory, "maps.json"));
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        foreach (var table in _tables)
        {
            await PostgisTestDatabase.ExecuteAsync($"DROP TABLE IF EXISTS {Qualified(table)} CASCADE");
        }

        await PostgisTestDatabase.ExecuteAsync(
            $"CREATE TABLE {Qualified(Survey)} (\"id\" bigint PRIMARY KEY, \"name\" text, \"geom\" geometry(PointZ,4326)); "
            + $"INSERT INTO {Qualified(Survey)} VALUES (1, 'summit', ST_SetSRID(ST_MakePoint(13.4, 52.5, 46.0), 4326))");
        await PostgisTestDatabase.ExecuteAsync(
            $"CREATE TABLE {Qualified(Track)} (\"id\" bigint PRIMARY KEY, \"geom\" geometry(PointZM,4326)); "
            + $"INSERT INTO {Qualified(Track)} VALUES (1, ST_SetSRID(ST_MakePoint(13.4, 52.5, 46.0, 12.5), 4326))");
        await PostgisTestDatabase.ExecuteAsync(
            $"CREATE TABLE {Qualified(Station)} (\"id\" bigint PRIMARY KEY, \"geom\" geometry(Point,4326)); "
            + $"INSERT INTO {Qualified(Station)} VALUES (1, ST_SetSRID(ST_MakePoint(13.4, 52.5), 4326))");
        await PostgisTestDatabase.ExecuteAsync(
            $"CREATE TABLE {Qualified(Unconstrained)} (\"id\" bigint PRIMARY KEY, \"geom\" geometry); "
            + $"INSERT INTO {Qualified(Unconstrained)} VALUES (1, ST_SetSRID(ST_MakePoint(13.4, 52.5, 46.0), 4326))");

        var response = await client.PutAsync(
            $"/api/maps/{Service}",
            new StringContent(
                JsonSerializer.Serialize(new
                {
                    name = Service,
                    store = "postgis",
                    services = FeatureAndMapService,
                    layers = new object[]
                    {
                        new { dataset = Survey, layerId = 0 },
                        new { dataset = Station, layerId = 1 },
                        new { dataset = Track, layerId = 2 },
                        new { dataset = Unconstrained, layerId = 3 },
                    },
                }),
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // A second publication over the same datasets serves the same
        // descriptions through the MapServer surface, which carries the same
        // keys as the FeatureServer layer resource (SpatialEngine-fhf.3).
        var mapResponse = await client.PutAsync(
            $"/api/maps/{MapService}",
            new StringContent(
                JsonSerializer.Serialize(new
                {
                    name = MapService,
                    store = "postgis",
                    services = MapOnlyService,
                    layers = new object[]
                    {
                        new { dataset = Survey, layerId = 0 },
                        new { dataset = Station, layerId = 1 },
                        new { dataset = Track, layerId = 2 },
                        new { dataset = Unconstrained, layerId = 3 },
                    },
                }),
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.OK, mapResponse.StatusCode);
    }

    public void Dispose() => _factory?.Dispose();

    public async Task DisposeAsync()
    {
        if (PostgisTestDatabase.Available)
        {
            foreach (var table in _tables)
            {
                await PostgisTestDatabase.ExecuteAsync($"DROP TABLE IF EXISTS {Qualified(table)} CASCADE");
            }
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_directory is not null)
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [SkippableFact]
    public async Task A_declared_z_column_advertises_hasZ()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");

        var layer = await LayerAsync(0);

        Assert.True(layer.GetProperty("hasZ").GetBoolean());
        Assert.False(layer.TryGetProperty("hasM", out _));
    }

    [SkippableFact]
    public async Task A_declared_zm_column_advertises_both_ordinates()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");

        var layer = await LayerAsync(2);

        Assert.True(layer.GetProperty("hasZ").GetBoolean());
        Assert.True(layer.GetProperty("hasM").GetBoolean());
    }

    [SkippableFact]
    public async Task A_two_dimensional_dataset_advertises_no_ordinates()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");

        var layer = await LayerAsync(1);

        Assert.False(layer.TryGetProperty("hasZ", out _));
        Assert.False(layer.TryGetProperty("hasM", out _));
    }

    [SkippableFact]
    public async Task An_unconstrained_geometry_column_advertises_no_ordinates()
    {
        // The column is declared plain `geometry`, so nothing in the schema
        // proves a Z. Advertising one anyway would be the claim the whole
        // honesty rule exists to prevent.
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");

        var layer = await LayerAsync(3);

        Assert.False(layer.TryGetProperty("hasZ", out _));
        Assert.False(layer.TryGetProperty("hasM", out _));
    }

    [SkippableFact]
    public async Task The_store_reports_the_layout_it_learned_from_the_catalog()
    {
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        Assert.Equal("xyz", await LayoutAsync(client, Survey));
        Assert.Equal("xyzm", await LayoutAsync(client, Track));
        Assert.Equal("xy", await LayoutAsync(client, Station));
        Assert.Equal("xy", await LayoutAsync(client, Unconstrained));
    }

    [SkippableFact]
    public async Task The_map_server_layer_advertises_the_same_ordinates_as_the_feature_server()
    {
        // The map surface describes the same datasets, so a 3D dataset must not
        // describe itself as 2D there (SpatialEngine-fhf.3, ADR-0125).
        Skip.If(!PostgisTestDatabase.Available, PostgisTestDatabase.SkipReason ?? "no reason");

        var z = await MapLayerAsync(0);
        Assert.True(z.GetProperty("hasZ").GetBoolean());
        Assert.False(z.TryGetProperty("hasM", out _));

        var zm = await MapLayerAsync(2);
        Assert.True(zm.GetProperty("hasZ").GetBoolean());
        Assert.True(zm.GetProperty("hasM").GetBoolean());

        var twoDimensional = await MapLayerAsync(1);
        Assert.False(twoDimensional.TryGetProperty("hasZ", out _));
        Assert.False(twoDimensional.TryGetProperty("hasM", out _));

        var unconstrained = await MapLayerAsync(3);
        Assert.False(unconstrained.TryGetProperty("hasZ", out _));
        Assert.False(unconstrained.TryGetProperty("hasM", out _));
    }

    /// <summary>The layout the store learned from the declared column type, as the host API's lowercase enum.</summary>
    private static async Task<string> LayoutAsync(HttpClient client, string dataset)
    {
        var response = await client.GetAsync($"/api/datasets/{dataset}?store=postgis");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("geometryLayout").GetString()!;
    }

    private async Task<JsonElement> LayerAsync(int layerId)
    {
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.GetAsync($"{Root}/{Service}/FeatureServer/{layerId}?f=json");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private async Task<JsonElement> MapLayerAsync(int layerId)
    {
        using var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.GetAsync($"{Root}/{MapService}/MapServer/{layerId}?f=json");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static string Qualified(string dataset)
    {
        var parts = dataset.Split('.');
        return $"\"{parts[0]}\".\"{parts[1]}\"";
    }

    /// <summary>A host over the containerised PostGIS with an admin token and a private map file.</summary>
    private sealed class HasZFactory(string mapsPath) : WebApplicationFactory<Program>
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
