using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Host.Tests;

/// <summary>
/// The GeoServices Feature Service editing operations (spec §9.1.6–§9.1.9,
/// ADR-0037) over HTTP: <c>addFeatures</c>, <c>updateFeatures</c>,
/// <c>deleteFeatures</c> and <c>applyEdits</c>, the per-feature result
/// shape, capability advertising, rollback and the read-only rejection. The
/// writable in-memory store stands in for PostGIS so the conformance
/// fixtures do not need Docker. Each test gets a fresh store.
/// </summary>
public sealed class GeoServicesEditTests
{
    private const string Root = "/arcgis/rest/services/editable";

    [Fact]
    public async Task The_editable_layer_advertises_editing_capabilities()
    {
        using var context = new EditableContext();
        var layer = await context.GetJsonAsync($"{Root}/FeatureServer/0?f=json");

        Assert.Contains("Create", layer.GetProperty("capabilities").GetString());
        Assert.Contains("Update", layer.GetProperty("capabilities").GetString());
        Assert.Contains("Delete", layer.GetProperty("capabilities").GetString());
        var name = layer.GetProperty("fields").EnumerateArray().Single(field => field.GetProperty("name").GetString() == "name");
        Assert.True(name.GetProperty("editable").GetBoolean());
    }

    [Fact]
    public async Task Add_features_assigns_an_object_id()
    {
        using var context = new EditableContext();
        var result = await context.PostFormAsync(
            $"{Root}/FeatureServer/0/addFeatures",
            ("features", """[{"attributes":{"name":"Vienna","population":1900000},"geometry":{"x":16.3738,"y":48.2082}}]"""),
            ("f", "json"));

        var added = Assert.Single(result.GetProperty("addResults").EnumerateArray());
        Assert.True(added.GetProperty("success").GetBoolean());
        Assert.True(added.GetProperty("objectId").GetInt64() >= 4);

        var count = await context.GetJsonAsync($"{Root}/FeatureServer/0/query?returnCountOnly=true&f=json");
        Assert.True(count.GetProperty("count").GetInt32() >= 4);
    }

    [Fact]
    public async Task Update_features_merges_partial_attributes()
    {
        using var context = new EditableContext();
        var result = await context.PostFormAsync(
            $"{Root}/FeatureServer/0/updateFeatures",
            ("features", """[{"attributes":{"OBJECTID":2,"population":9999999}}]"""),
            ("f", "json"));

        var updated = Assert.Single(result.GetProperty("updateResults").EnumerateArray());
        Assert.True(updated.GetProperty("success").GetBoolean());
        Assert.Equal(2, updated.GetProperty("objectId").GetInt64());

        var query = await context.GetJsonAsync($"{Root}/FeatureServer/0/query?where=" + Uri.EscapeDataString("name = 'Paris'") + "&f=json");
        var feature = Assert.Single(query.GetProperty("features").EnumerateArray());
        Assert.Equal(9999999, feature.GetProperty("attributes").GetProperty("population").GetInt64());
    }

    [Fact]
    public async Task Delete_features_reports_each_object_id()
    {
        using var context = new EditableContext();
        var result = await context.PostFormAsync(
            $"{Root}/FeatureServer/0/deleteFeatures",
            ("objectIds", "3"),
            ("f", "json"));

        var deleted = Assert.Single(result.GetProperty("deleteResults").EnumerateArray());
        Assert.True(deleted.GetProperty("success").GetBoolean());
        Assert.Equal(3, deleted.GetProperty("objectId").GetInt64());
        var count = (await context.GetJsonAsync($"{Root}/FeatureServer/0/query?returnCountOnly=true&f=json")).GetProperty("count").GetInt32();
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Apply_edits_returns_all_three_result_arrays()
    {
        using var context = new EditableContext();
        var result = await context.PostFormAsync(
            $"{Root}/FeatureServer/0/applyEdits",
            ("adds", """[{"attributes":{"name":"Oslo","population":700000},"geometry":{"x":10.7522,"y":59.9139}}]"""),
            ("updates", """[{"attributes":{"OBJECTID":1,"population":3700000}}]"""),
            ("deletes", "[2]"),
            ("f", "json"));

        Assert.True(result.TryGetProperty("addResults", out var adds));
        Assert.True(result.TryGetProperty("updateResults", out var updates));
        Assert.True(result.TryGetProperty("deleteResults", out var deletes));
        Assert.True(Assert.Single(adds.EnumerateArray()).GetProperty("success").GetBoolean());
        Assert.True(Assert.Single(updates.EnumerateArray()).GetProperty("success").GetBoolean());
        Assert.True(Assert.Single(deletes.EnumerateArray()).GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task A_failed_feature_fails_alone_without_rollback()
    {
        using var context = new EditableContext();
        var result = await context.PostFormAsync(
            $"{Root}/FeatureServer/0/addFeatures",
            ("features", """[{"attributes":{"name":"Valid"},"geometry":{"x":0,"y":0}},{"attributes":{"population":1},"geometry":{"x":1,"y":1}}]"""),
            ("f", "json"));

        var results = result.GetProperty("addResults").EnumerateArray().ToArray();
        Assert.True(results[0].GetProperty("success").GetBoolean());
        Assert.False(results[1].GetProperty("success").GetBoolean());
        Assert.Equal(400, results[1].GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Rollback_on_failure_undoes_the_whole_batch()
    {
        using var context = new EditableContext();
        var before = (await context.GetJsonAsync($"{Root}/FeatureServer/0/query?returnCountOnly=true&f=json")).GetProperty("count").GetInt32();

        var result = await context.PostFormAsync(
            $"{Root}/FeatureServer/0/addFeatures",
            ("features", """[{"attributes":{"name":"RollbackMe"},"geometry":{"x":0,"y":0}},{"attributes":{"population":1},"geometry":{"x":1,"y":1}}]"""),
            ("rollbackOnFailure", "true"),
            ("f", "json"));

        Assert.All(result.GetProperty("addResults").EnumerateArray(), added => Assert.False(added.GetProperty("success").GetBoolean()));
        var after = (await context.GetJsonAsync($"{Root}/FeatureServer/0/query?returnCountOnly=true&f=json")).GetProperty("count").GetInt32();
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task A_read_only_service_rejects_edits()
    {
        using var factory = new EditableFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsync(
            "/arcgis/rest/services/demo/FeatureServer/0/addFeatures",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("features", "[]"), new KeyValuePair<string, string>("f", "json")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("read-only", (await ErrorAsync(response)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_malformed_features_payload_is_rejected()
    {
        using var context = new EditableContext();
        var response = await context.Client.PostAsync(
            $"{Root}/FeatureServer/0/addFeatures",
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("features", "not-json"),
                new KeyValuePair<string, string>("f", "json"),
            ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("features", (await ErrorAsync(response)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task Use_global_ids_is_rejected()
    {
        using var context = new EditableContext();
        var response = await context.Client.PostAsync(
            $"{Root}/FeatureServer/0/updateFeatures",
            new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("features", "[]"),
                new KeyValuePair<string, string>("useGlobalIds", "true"),
                new KeyValuePair<string, string>("f", "json"),
            ]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("useGlobalIds", (await ErrorAsync(response)).GetProperty("message").GetString());
    }

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.GetProperty("error");
    }

    /// <summary>One test's fresh factory, client and writable store.</summary>
    private sealed class EditableContext : IDisposable
    {
        private readonly EditableFactory _factory = new();

        public EditableContext() => Client = _factory.CreateClient();

        public HttpClient Client { get; }

        public async Task<JsonElement> GetJsonAsync(string path)
        {
            var response = await Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        public async Task<JsonElement> PostFormAsync(string path, params (string Key, string Value)[] values)
        {
            var response = await Client.PostAsync(path, new FormUrlEncodedContent(values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value))));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        }

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
        }
    }

    /// <summary>Mounts an extra <c>editable</c> FeatureServer backed by the writable in-memory store.</summary>
    public sealed class EditableFactory : WebApplicationFactory<Program>
    {
        private readonly WritableMemoryStore _store = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Spatial:GeoServices:Services:1:Name"] = "editable",
                    ["Spatial:GeoServices:Services:1:Store"] = "editable",
                    ["Spatial:GeoServices:Services:1:Type"] = "FeatureServer",
                }));
            builder.ConfigureServices(services =>
            {
                services.AddKeyedSingleton<IDataCatalogue>("editable", _store);
                services.AddKeyedSingleton<IFeatureStore>("editable", _store);
                services.AddKeyedSingleton<IFeatureEditStore>("editable", new WritableMemoryEditor(_store));
                services.AddKeyedSingleton<ITransactionStore>("editable", _store);
            });
        }
    }
}
