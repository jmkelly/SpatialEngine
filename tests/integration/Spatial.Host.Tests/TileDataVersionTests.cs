using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.Core.Geometry;

namespace Spatial.Host.Tests;

/// <summary>
/// The data version in the tile cache key (ADR-0083): a rendered tile is
/// derived from feature data, so its key must fold in the datasets' content
/// versions, not only the request that produced it. A write moves a version, so
/// the next request for the affected tiles misses the cache and re-renders —
/// no <c>DELETE /api/render/cache</c> needed — while an unchanged request, or
/// one over an untouched dataset, still hits it.
/// </summary>
public sealed class TileDataVersionTests : IDisposable
{
    private const string Token = "test-admin-token";

    private const string Store = "memory";

    private const string Circle =
        """[{"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#ff0000","circle-radius":20,"circle-opacity":1.0}}]""";

    private const string BigCircle =
        """[{"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#00ff00","circle-radius":60,"circle-opacity":1.0}}]""";

    private static readonly string[] TileServices = ["tiles"];

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-tile-versions-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private WebApplicationFactory<Program> Factory() =>
        new DataVersionFactory(Path.Combine(_directory, $"maps-{Guid.NewGuid():N}.json"));

    private static string NewDataset() => $"public.places_{Guid.NewGuid():N}";

    private static Feature Point(string name, double x, double y) =>
        new(
            FeatureId.Unassigned,
            Schema,
            [
                AttributeValue.FromString(name),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326))),
            ]);

    /// <summary>A fresh memory dataset holding one point, so a tile has something to draw.</summary>
    private static async Task<string> DatasetWithPointAsync(HttpClient client, string name)
    {
        var dataset = NewDataset();
        await PostAsync(client, new CreateDatasetRequest(dataset, Encoded([]), 4326), $"/api/datasets?store={Store}");
        await WriteAsync(client, dataset, Point(name, 13.405, 52.52));
        return dataset;
    }

    private static async Task WriteAsync(HttpClient client, string dataset, params Feature[] features) =>
        await PostAsync(
            client,
            new FeatureWriteRequest(dataset, Encoded(features)),
            $"/api/features/write?store={Store}");

    private static string Encoded(IReadOnlyList<Feature> features) =>
        Convert.ToBase64String(FeatureBatchCodec.Encode(new FeatureBatch(Schema, features)));

    private static async Task PostAsync(HttpClient client, object body, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task PutMapAsync(HttpClient client, string name, string dataset, string? style = Circle)
    {
        var body = JsonSerializer.Serialize(new
        {
            name,
            store = Store,
            services = TileServices,
            layers = new[] { new { dataset, layerId = 0, name = "places", style } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/maps/{name}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string Tile(string map) => $"/api/maps/{map}/tiles/0/0/0.png";

    private static string VectorTile(string map) => $"/api/maps/{map}/tiles/mvt/0/0/0.pbf";

    private static string Cached(HttpResponseMessage response) => response.Headers.GetValues("X-Tile-Cached").Single();

    [Fact]
    public async Task A_write_misses_the_cache_and_re_renders_the_affected_tile()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var dataset = await DatasetWithPointAsync(client, "Berlin");
        await PutMapAsync(client, "versioned", dataset);
        using var first = await client.GetAsync(Tile("versioned"));
        Assert.Equal("false", Cached(first));
        var before = await first.Content.ReadAsByteArrayAsync();

        using var second = await client.GetAsync(Tile("versioned"));
        Assert.Equal("true", Cached(second));
        Assert.Equal(before, await second.Content.ReadAsByteArrayAsync());

        await WriteAsync(client, dataset, Point("Perth", 115.86, -31.95));
        using var third = await client.GetAsync(Tile("versioned"));

        Assert.Equal("false", Cached(third));
        Assert.NotEqual(before, await third.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_write_leaves_another_datasets_tile_cached()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var watched = await DatasetWithPointAsync(client, "Berlin");
        var other = await DatasetWithPointAsync(client, "Paris");
        await PutMapAsync(client, "watched", watched);
        await PutMapAsync(client, "other", other);
        using var warm = await client.GetAsync(Tile("other"));
        Assert.Equal("false", Cached(warm));

        await WriteAsync(client, watched, Point("Perth", 115.86, -31.95));

        using var response = await client.GetAsync(Tile("other"));
        Assert.Equal("true", Cached(response));
    }

    [Fact]
    public async Task A_write_misses_the_vector_tile_cache_too()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var dataset = await DatasetWithPointAsync(client, "Berlin");
        await PutMapAsync(client, "vectors", dataset);
        using var first = await client.GetAsync(VectorTile("vectors"));
        Assert.Equal("false", Cached(first));
        var before = await first.Content.ReadAsByteArrayAsync();

        using var second = await client.GetAsync(VectorTile("vectors"));
        Assert.Equal("true", Cached(second));
        Assert.Equal(before, await second.Content.ReadAsByteArrayAsync());

        await WriteAsync(client, dataset, Point("Perth", 115.86, -31.95));
        using var third = await client.GetAsync(VectorTile("vectors"));

        Assert.Equal("false", Cached(third));
        Assert.NotEqual(before, await third.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_style_save_on_a_layer_still_invalidates_that_maps_tiles()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var dataset = await DatasetWithPointAsync(client, "Berlin");
        await PutMapAsync(client, "restyled", dataset);
        using var warm = await client.GetAsync(Tile("restyled"));
        Assert.Equal("false", Cached(warm));

        await PutMapAsync(client, "restyled", dataset, BigCircle);

        using var response = await client.GetAsync(Tile("restyled"));
        Assert.Equal("false", Cached(response));
    }

    [Fact]
    public async Task A_cancelled_tile_request_caches_nothing()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var dataset = await DatasetWithPointAsync(client, "Berlin");
        await PutMapAsync(client, "cancelled", dataset);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync(Tile("cancelled"), cancellation.Token));

        using var response = await client.GetAsync(Tile("cancelled"));
        Assert.Equal("false", Cached(response));
    }

    [Fact]
    public async Task A_store_without_a_content_version_still_caches()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name = "fallback",
            store = "demo",
            services = TileServices,
            layers = new[] { new { dataset = "demo.cities", layerId = 0, name = "cities", style = (string?)null } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/maps/fallback")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var saved = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        using var first = await client.GetAsync(Tile("fallback"));
        using var second = await client.GetAsync(Tile("fallback"));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("false", Cached(first));
        Assert.Equal("true", Cached(second));
    }

    [Fact]
    public async Task A_tile_reports_the_content_version_it_was_rendered_at()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        var dataset = await DatasetWithPointAsync(client, "Berlin");
        await PutMapAsync(client, "reported", dataset);

        using var first = await client.GetAsync(Tile("reported"));
        var before = first.Headers.GetValues("X-Tile-Version").Single();
        Assert.Equal(64, before.Length);

        await WriteAsync(client, dataset, Point("Perth", 115.86, -31.95));
        using var second = await client.GetAsync(Tile("reported"));

        Assert.NotEqual(before, second.Headers.GetValues("X-Tile-Version").Single());
    }

    /// <summary>A host with an admin token and a per-test map file.</summary>
    private sealed class DataVersionFactory(string mapsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
        }
    }
}
