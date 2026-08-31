using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Codec;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Operations;

namespace Spatial.Host.Tests;

/// <summary>
/// The browser replacement demonstration over HTTP (plan §17.9-11, ADR-0031):
/// both NTS versions activate side by side, new work routes to the preferred
/// version (active-preference routing), a version drains without stopping the
/// host, and rollback reactivates the drained version and routes back. The
/// flow runs entirely through the public plugin-control endpoints — the same
/// surface the workbench's runtime-status screen drives.
/// </summary>
public sealed class PluginReplacementTests : IDisposable
{
    private readonly string _root;
    private readonly WebApplicationFactory<Program> _factory;

    public PluginReplacementTests()
    {
        _root = Directory.CreateTempSubdirectory("spatial-replace-").FullName;
        NtsHostPackageWriter.WritePair(_root);
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("Spatial:PackagesRoot", _root));
    }

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task Both_versions_start_side_by_side_and_v1_serves_new_work_by_default()
    {
        var plugins = await GetPluginsAsync();
        Assert.Equal(["nts@1", "nts@2"], plugins.Select(plugin => plugin.Id).ToArray());
        Assert.All(plugins, plugin => Assert.Equal("Active", plugin.State));

        var provenance = await BufferProvenanceAsync();
        Assert.Equal("nts@1", provenance);
    }

    [Fact]
    public async Task Route_new_work_switches_provenance_to_v2()
    {
        await PostAsync("/api/plugins/nts@2/route-new-work");

        var updated = await GetPluginAsync("nts@2");
        Assert.Equal("Active", updated.State);

        Assert.Equal("nts@2", await BufferProvenanceAsync());
        Assert.Equal("nts@2", await BufferProvenanceAsync());
    }

    [Fact]
    public async Task Routing_to_an_unknown_plugin_is_404()
    {
        var response = await Client.PostAsync("/api/plugins/nope@9/route-new-work", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Routing_to_a_drained_plugin_is_409()
    {
        var drain = await Client.PostAsync("/api/plugins/nts@1/drain", null);
        Assert.Equal(HttpStatusCode.OK, drain.StatusCode);

        var response = await Client.PostAsync("/api/plugins/nts@1/route-new-work", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Drain_stops_the_worker_but_the_host_keeps_serving()
    {
        // (1) route new work to v2, (2) drain v1 — the demonstration's drain
        // step must not stop the host or the browser-facing API.
        await PostAsync("/api/plugins/nts@2/route-new-work");
        Assert.Equal("nts@2", await BufferProvenanceAsync());

        var drained = await PostAsync("/api/plugins/nts@1/drain");
        Assert.Equal("Stopped", drained.State);

        // The host keeps running and serves through the other version.
        var health = await Client.GetFromJsonAsync<JsonObject>("/health/ready");
        Assert.Equal("ready", health?["status"]?.GetValue<string>());
        Assert.Equal("nts@2", await BufferProvenanceAsync());
    }

    [Fact]
    public async Task Rollback_reactivates_the_drained_version_and_routes_back()
    {
        await PostAsync("/api/plugins/nts@2/route-new-work");
        await PostAsync("/api/plugins/nts@1/drain");
        Assert.Equal("nts@2", await BufferProvenanceAsync());

        var rolledBack = await PostAsync("/api/plugins/nts@1/rollback");
        Assert.Equal("Active", rolledBack.State);

        Assert.Equal("nts@1", await BufferProvenanceAsync());
    }

    [Fact]
    public async Task Control_endpoints_without_a_supervisor_are_404()
    {
        using var noPlugins = new WebApplicationFactory<Program>();
        var client = noPlugins.CreateClient();

        foreach (var path in new[] { "/api/plugins/nts@1/route-new-work", "/api/plugins/nts@1/drain", "/api/plugins/nts@1/rollback" })
        {
            var response = await client.PostAsync(path, null);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    /// <summary>Buffers a unit point and returns the serving provider from the provenance.</summary>
    private async Task<string?> BufferProvenanceAsync()
    {
        var point = GeometryFactory.CreatePoint(0, 0);
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest(
                "spatial.geometry.buffer@1",
                Arguments: new Dictionary<string, JsonNode?>
                {
                    [GeometryOperationArguments.Geometry] = ValueCodec.Encode(point),
                    [GeometryOperationArguments.Distance] = JsonValue.Create(1.0),
                }),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.NotNull(body);
        Assert.True(body.Ok, body.Error?.Message ?? "invocation failed");
        Assert.Equal("completed", body.Kind);
        return body.Provenance?.Provider;
    }

    private async Task<PluginDto[]> GetPluginsAsync() =>
        (await Client.GetFromJsonAsync<PluginDto[]>("/api/plugins", HostApiJson.Options)) ?? [];

    private async Task<PluginDto> GetPluginAsync(string id) =>
        (await Client.GetFromJsonAsync<PluginDto>($"/api/plugins/{id}", HostApiJson.Options))!;

    private async Task<PluginDto> PostAsync(string path)
    {
        var response = await Client.PostAsync(path, null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PluginDto>(HostApiJson.Options))!;
    }

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
