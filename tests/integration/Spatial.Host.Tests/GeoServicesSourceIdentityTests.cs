using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.Memory;

namespace Spatial.Host.Tests;

/// <summary>
/// A source-identity ingest served end-to-end (ADR-0038, ADR-0112 point 2).
/// The upload is a GeoJSON whose <c>id</c> runs 10..13, ingested with
/// <c>identity=source&amp;identityField=id</c>, so the layer's
/// <c>OBJECTID</c> is the identity column while the decode numbered the
/// records 1..4 on the way in. A store that keys its features by the identity
/// column is then readable by <c>OBJECTID</c>: the per-feature resource, the
/// attachment resources and the relationship writes all resolve their targets
/// through the read-by-identity face, and none of them falls back to a
/// whole-dataset scan. The counting store proves both halves over HTTP.
/// </summary>
public sealed class GeoServicesSourceIdentityTests : IDisposable
{
    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";
    private const string Service = $"{Root}/srcid/FeatureServer";

    private const string Parents = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[13.4,52.5]},"properties":{"id":100,"name":"Berlin"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[2.35,48.85]},"properties":{"id":200,"name":"Paris"}}
        ]}
        """;

    private const string Children = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[13.41,52.51]},"properties":{"id":10,"parent_id":1,"name":"Mitte"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[2.36,48.86]},"properties":{"id":11,"parent_id":1,"name":"Kreuzberg"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[2.37,48.87]},"properties":{"id":12,"parent_id":2,"name":"Belleville"}}
        ]}
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-source-identity-").FullName;
    private readonly WebApplicationFactory<Program> _factory;

    public GeoServicesSourceIdentityTests() => _factory = new SourceIdentityFactory(Path.Combine(_directory, "maps.json"));

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private HttpClient Client() => _factory.CreateClient();

    /// <summary>
    /// The counters the host's read faces reported. The wrapper is resolved
    /// from the running host rather than handed to it, so it decorates the
    /// very store the ingest wrote to.
    /// </summary>
    private CountingStore Store => _factory.Services.GetRequiredService<CountingStore>();

    private static HttpRequestMessage Authorized(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<HttpResponseMessage> Ingest(HttpClient client, string dataset, string body, bool publish) =>
        await client.SendAsync(Authorized(
            HttpMethod.Post,
            $"/api/ingest?store=memory&dataset={dataset}&srid=4326&format=geojson&identity=source&identityField=id&publish={(publish ? "srcid" : "no")}",
            new StringContent(body, Encoding.UTF8, "application/json")));

    /// <summary>Ingests the two layers and declares the one-to-many relationship between them.</summary>
    private async Task DeclareAsync()
    {
        var client = Client();
        Assert.Equal(HttpStatusCode.OK, (await Ingest(client, "srcid.children", Children, publish: true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Ingest(client, "srcid.parents", Parents, publish: false)).StatusCode);
        await DeclareMapAsync(client);
    }

    /// <summary>Declares the map: parents on layer 0 with a relationship onto the children on layer 1.</summary>
    private static async Task DeclareMapAsync(HttpClient client)
    {
        var map = new
        {
            name = "srcid",
            store = "memory",
            layers = new object[]
            {
                new
                {
                    dataset = "srcid.parents", layerId = 0, name = "parents",
                    relationships = new object[]
                    {
                        new { name = "children", relatedLayerId = 1, primaryKeyColumn = "id", relatedKeyColumn = "parent_id" },
                    },
                },
                new { dataset = "srcid.children", layerId = 1, name = "children" },
            },
            services = new[] { "feature" },
        };
        var declared = await client.SendAsync(Authorized(
            HttpMethod.Put, "/api/maps/srcid",
            new StringContent(JsonSerializer.Serialize(map), Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.OK, declared.StatusCode);
    }

    /// <summary>
    /// The per-feature resource resolves object 10 through one lookup and
    /// never reads the whole layer: the identity column's value is the
    /// feature identity, so the targeted read resolves.
    /// </summary>
    [Fact]
    public async Task The_per_feature_resource_resolves_a_source_identity_object_id_through_one_lookup()
    {
        await DeclareAsync();
        Store.Reset();

        var body = await BodyAsync(await Client().GetAsync($"{Service}/1/10?f=json&outFields=id,name"));

        Assert.Equal("Mitte", body.GetProperty("feature").GetProperty("attributes").GetProperty("name").GetString());
        Assert.Equal(10, body.GetProperty("feature").GetProperty("attributes").GetProperty("id").GetInt32());
        Assert.Equal(1, Store.Lookups);
        Assert.Equal(0, Store.Scans);
    }

    /// <summary>
    /// The per-feature attachment resource is the same keyed read: the found
    /// target costs one lookup and no scan.
    /// </summary>
    [Fact]
    public async Task The_attachment_resource_resolves_a_source_identity_object_id_through_one_lookup()
    {
        await DeclareAsync();
        var store = Store;
        store.Reset();
        var client = Client();

        var body = await BodyAsync(await client.GetAsync($"{Service}/1/12/attachments?f=json"));

        Assert.Empty(body.GetProperty("attachmentInfos").EnumerateArray());
        Assert.Equal(1, store.Lookups);
        Assert.Equal(0, store.Scans);
    }

    /// <summary>
    /// An object id no feature carries is still <c>not.found</c>, on a
    /// source-identity layer as on any other: a targeted read that resolves
    /// nothing is not evidence of absence, so the scan decides (ADR-0112
    /// point 5).
    /// </summary>
    [Fact]
    public async Task An_unknown_attachment_object_id_on_a_source_identity_layer_is_not_found()
    {
        await DeclareAsync();
        var client = Client();

        var missing = await client.GetAsync($"{Service}/1/99/attachments?f=json");

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    /// <summary>
    /// The relationship writes resolve their origin records by the same
    /// lookup, so a relate on a source-identity layer moves the key and the
    /// read follows it. The related layer is read whole once for the
    /// traversal — that is the traversal's own read, not a target resolution
    /// — so only the lookups are asserted here.
    /// </summary>
    [Fact]
    public async Task Relate_resolves_its_origin_records_through_the_lookup()
    {
        await DeclareAsync();
        var store = Store;
        store.Reset();
        var client = Client();

        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/0/relate?f=json&objectIds=100&relationshipId=children&relateIds=12",
            new StringContent(string.Empty)));
        var result = Assert.Single((await BodyAsync(response)).GetProperty("results").EnumerateArray());

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(100, result.GetProperty("objectId").GetInt64());
        Assert.Equal(12, result.GetProperty("relateId").GetInt64());
        Assert.True(store.Lookups > 0, "the relate resolved its origin records without a read-by-identity");
    }

    /// <summary>
    /// The edit path resolves its targets through the same lookup
    /// (ADR-0038), so a partial update of object 12 on a source-identity
    /// layer finds the feature that exists rather than reporting it missing,
    /// and the read follows the edit.
    /// </summary>
    [Fact]
    public async Task Update_resolves_a_source_identity_object_id_through_the_lookup()
    {
        await DeclareAsync();
        var store = Store;
        store.Reset();
        var client = Client();

        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/1/updateFeatures?f=json",
            new StringContent(
                """{"features":[{"attributes":{"OBJECTID":12,"name":"Belleville Nord"}}],"rollbackOnFailurePolicy":"stopOnError"}""",
                Encoding.UTF8,
                "application/json")));
        var result = Assert.Single((await BodyAsync(response)).GetProperty("updateResults").EnumerateArray());

        Assert.True(result.GetProperty("success").GetBoolean(), "the update resolved its target through a read-by-identity");
        Assert.Equal(12, result.GetProperty("objectId").GetInt64());
        Assert.True(store.Lookups > 0, "the update read its target by identity");
        Assert.Equal(0, store.Scans);

        var body = await BodyAsync(await client.GetAsync($"{Service}/1/12?f=json&outFields=id,name"));
        Assert.Equal("Belleville Nord", body.GetProperty("feature").GetProperty("attributes").GetProperty("name").GetString());
    }

    /// <summary>
    /// The same layer over HTTP with a store that has no read-by-identity
    /// face: the per-feature answer is identical, which is what makes the
    /// lookup a change of cost and not of result.
    /// </summary>
    [Fact]
    public async Task The_per_feature_resource_answers_the_same_without_a_lookup()
    {
        using var factory = new SourceIdentityFactory(Path.Combine(_directory, "maps2.json"), lookup: false);
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await Ingest(client, "srcid.children", Children, publish: true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Ingest(client, "srcid.parents", Parents, publish: false)).StatusCode);
        await DeclareMapAsync(client);

        var store = factory.Services.GetRequiredService<CountingStore>();
        var body = await BodyAsync(await client.GetAsync($"{Service}/1/10?f=json&outFields=id,name"));

        Assert.Equal("Mitte", body.GetProperty("feature").GetProperty("attributes").GetProperty("name").GetString());
        Assert.Equal(0, store.Lookups);
        Assert.Equal(1, store.Scans);
    }

    private sealed class SourceIdentityFactory(string mapsPath, bool lookup = true)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
            builder.ConfigureServices(services =>
            {
                // Only the read faces are wrapped, and around the very store
                // the host registered — so the ingest writes to the store the
                // counters are counting. The edit, attachment and ingest
                // faces stay the host's own.
                services.AddSingleton(provider => new CountingStore(provider.GetRequiredService<MemoryStore>()));
                services.AddKeyedSingleton<IDataCatalogue>("memory", (provider, _) => provider.GetRequiredService<CountingStore>());
                services.AddKeyedSingleton<IFeatureStore>("memory", (provider, _) => lookup
                    ? provider.GetRequiredService<CountingStore>()
                    : new ScanOnlyReadStore(provider.GetRequiredService<CountingStore>()));
                if (lookup)
                {
                    services.AddKeyedSingleton<IFeatureLookup>("memory", (provider, _) => provider.GetRequiredService<CountingStore>());
                }
            });
        }
    }
}

/// <summary>
/// The memory store's read faces with counters, so a hosted test can prove
/// which of them answered (ADR-0038, ADR-0112).
/// </summary>
public sealed class CountingStore : IDataCatalogue, IFeatureStore, IFeatureLookup
{
    private readonly MemoryStore _store;

    public CountingStore(MemoryStore store)
    {
        _store = store;
    }

    /// <summary>How many whole-dataset reads were served.</summary>
    public int Scans { get; private set; }

    /// <summary>How many read-by-identity calls were served.</summary>
    public int Lookups { get; private set; }

    public void Reset()
    {
        Scans = 0;
        Lookups = 0;
    }

    public Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default) =>
        _store.ListAsync(pattern, cancellationToken);

    public Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default) =>
        _store.DescribeAsync(dataset, cancellationToken);

    public Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default) =>
        _store.CreateAsync(dataset, sample, srid, cancellationToken);

    public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
    {
        Scans++;
        return _store.ScanAsync(dataset, cancellationToken);
    }

    public Task<FeatureQueryPage> QueryAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
        _store.QueryAsync(dataset, query, cancellationToken);

    public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
        _store.WriteAsync(dataset, batch, transaction, cancellationToken);

    public Task<IReadOnlyList<Feature>> GetAsync(string dataset, IReadOnlyList<FeatureId> ids, CancellationToken cancellationToken = default)
    {
        Lookups++;
        return _store.GetAsync(dataset, ids, cancellationToken);
    }
}

/// <summary>
/// The same store without the read-by-identity face, so the facade has to
/// resolve its targets by scanning (ADR-0038).
/// </summary>
public sealed class ScanOnlyReadStore(CountingStore store) : IFeatureStore
{
    public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default) =>
        store.ScanAsync(dataset, cancellationToken);

    public Task<FeatureQueryPage> QueryAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
        store.QueryAsync(dataset, query, cancellationToken);

    public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
        store.WriteAsync(dataset, batch, transaction, cancellationToken);
}
