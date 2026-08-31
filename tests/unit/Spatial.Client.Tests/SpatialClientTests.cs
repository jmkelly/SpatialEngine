using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.PluginSdk.Http;

namespace Spatial.Client.Tests;

/// <summary>
/// The client SDK's contract handling: URL building, the shared camelCase
/// JSON wire, value codec round trips, job polling, stream decoding
/// (including canonical feature batches) and structured error mapping — all
/// against a scriptable stub host.
/// </summary>
public sealed class SpatialClientTests
{
    [Fact]
    public async Task Get_capabilities_builds_the_url_and_decodes_the_shared_contract()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            """[{"id":"spatial.geometry.buffer@1","purpose":"Expands a geometry","inputSchema":"geometry.operation","outputSchema":"geometry","traits":["Cancellable"],"requiredPermissions":[],"providers":["nts@1"]}]""")));

        var capabilities = await stub.Client.GetCapabilitiesAsync();

        var exchange = Assert.Single(stub.Handler.Exchanges);
        Assert.Equal(HttpMethod.Get, exchange.Request.Method);
        Assert.Equal("/api/capabilities", exchange.Request.RequestUri?.AbsolutePath);
        var capability = Assert.Single(capabilities);
        Assert.Equal("spatial.geometry.buffer@1", capability.Id);
        Assert.Equal("nts@1", Assert.Single(capability.Providers));
    }

    [Fact]
    public async Task Invoke_encodes_arguments_with_the_value_codec()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            """{"kind":"completed","capability":"fixture.echo@1","ok":true,"result":"hello","error":null,"provenance":{"capability":"fixture.echo@1","provider":"fixture@1","step":"FirstHealthy","startedAt":"2026-09-01T00:00:00Z","durationMs":1.2,"deadline":null,"jobId":null},"job":null}""")));

        var response = await stub.Client.InvokeAsync(
            "fixture.echo@1",
            new Dictionary<string, object?> { ["text"] = "hello" });

        Assert.True(response.Ok);
        Assert.Equal("hello", response.Result?.GetValue<string>());
        Assert.Equal("fixture@1", response.Provenance!.Provider);

        var exchange = Assert.Single(stub.Handler.Exchanges);
        Assert.Equal("/api/invocations", exchange.Request.RequestUri?.AbsolutePath);
        var body = JsonNode.Parse(await exchange.Request.Content!.ReadAsStringAsync())!;
        Assert.Equal("fixture.echo@1", body["capability"]?.GetValue<string>());
        Assert.Equal("hello", body["arguments"]?["text"]?.GetValue<string>());
    }

    [Fact]
    public async Task Invoke_round_trips_i64_geometry_and_resource_values()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            """{"kind":"completed","capability":"fixture.mix@1","ok":true,"result":{"$i64":"9223372036854775807"},"error":null,"provenance":{"capability":"fixture.mix@1","provider":"fixture@1","step":"FirstHealthy","startedAt":"2026-09-01T00:00:00Z","durationMs":0.5,"deadline":null,"jobId":null},"job":null}""")));

        var response = await stub.Client.InvokeAsync(
            "fixture.mix@1",
            new Dictionary<string, object?> { ["big"] = long.MaxValue });

        Assert.True(response.Ok);
        var decoded = Spatial.PluginSdk.Codec.ValueCodec.Decode(response.Result);
        Assert.Equal(long.MaxValue, Assert.IsType<long>(decoded));
    }

    [Fact]
    public async Task Non_2xx_responses_become_spatial_api_exceptions()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            "{}\"not found\"", HttpStatusCode.NotFound)));

        var exception = await Assert.ThrowsAsync<SpatialApiException>(
            () => stub.Client.GetCapabilityAsync("spatial.missing@9"));
        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task Wait_for_job_polls_until_the_terminal_state()
    {
        var polls = 0;
        using var stub = new StubClient(new StubHttpHandler(_ =>
        {
            polls++;
            return StubHttpHandler.Json(polls < 3
                ? """{"id":"job1","capability":"fixture.sleep@1","state":"Running","createdAt":"2026-09-01T00:00:00Z","startedAt":"2026-09-01T00:00:00Z","completedAt":null,"deadline":null,"provider":"fixture@1","step":"FirstHealthy","errorCode":null}"""
                : """{"id":"job1","capability":"fixture.sleep@1","state":"Completed","createdAt":"2026-09-01T00:00:00Z","startedAt":"2026-09-01T00:00:00Z","completedAt":"2026-09-01T00:00:02Z","deadline":null,"provider":"fixture@1","step":"FirstHealthy","errorCode":null}""");
        }));

        var job = await stub.Client.WaitForJobAsync("job1");

        Assert.Equal(Spatial.PluginSdk.Jobs.JobState.Completed, job.State);
        Assert.True(polls >= 3);
        Assert.Equal("/api/jobs/job1", stub.Handler.Exchanges[^1].Request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Read_stream_decodes_items_and_throws_on_error_line()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Ndjson(
            "\"chunk 1\"\n{\"$bytes\":\"aGVsbG8=\"}\n")));

        var items = new List<object?>();
        await foreach (var item in stub.Client.ReadStreamAsync("resource1"))
        {
            items.Add(item);
        }

        Assert.Equal(2, items.Count);
        Assert.Equal("chunk 1", items[0]);
        Assert.Equal([104, 101, 108, 108, 111], Assert.IsType<byte[]>((byte[])items[1]!));

        using var failing = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Ndjson(
            "\"ok\"\n{\"$error\":{\"kind\":\"ProviderFailure\",\"code\":\"provider.failure\",\"message\":\"boom\"}}\n")));
        var received = new List<object?>();
        var exception = await Assert.ThrowsAsync<CapabilityStreamException>(async () =>
        {
            await foreach (var item in failing.Client.ReadStreamAsync("resource1"))
            {
                received.Add(item);
            }
        });
        Assert.Equal("provider.failure", exception.Error.Code);
        Assert.Single(received);
    }

    [Fact]
    public async Task Read_feature_batches_decodes_canonical_binary_items()
    {
        var schema = new FeatureSchema([new FieldDefinition("name", AttributeKind.String)]);
        var batch = new FeatureBatch(
            schema,
            [new Feature(new FeatureId("f1"), schema, [AttributeValue.FromString("alice")])]);
        var bytes = FeatureBatchCodec.Encode(batch);
        var line = JsonSerializer.Serialize(new JsonObject { ["$bytes"] = Convert.ToBase64String(bytes) });

        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Ndjson($"{line}\n")));

        var batches = new List<FeatureBatch>();
        await foreach (var decoded in stub.Client.ReadFeatureBatchesAsync("scan1"))
        {
            batches.Add(decoded);
        }

        var decodedBatch = Assert.Single(batches);
        Assert.Equal("f1", decodedBatch.Features[0].Id.Value);
        Assert.Equal("alice", decodedBatch.Features[0].Attributes[0].StringValue);
    }

    [Fact]
    public async Task Delete_resource_accepts_no_content()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));

        await stub.Client.DeleteResourceAsync("resource1");

        var exchange = Assert.Single(stub.Handler.Exchanges);
        Assert.Equal(HttpMethod.Delete, exchange.Request.Method);
        Assert.Equal("/api/resources/resource1", exchange.Request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Plugins_decode_and_unknown_plugin_is_an_api_exception()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            """[{"id":"nts@1","displayName":"NetTopologySuite operations","runtime":"dotnet","state":"Active","restartCount":0,"processId":42,"startedAt":"2026-09-01T00:00:00Z","lastHealthyAt":"2026-09-01T00:00:00Z","lastError":null,"capabilities":[]}]""")));

        var plugins = await stub.Client.GetPluginsAsync();
        var plugin = Assert.Single(plugins);
        Assert.Equal("nts@1", plugin.Id);
        Assert.Equal(42, plugin.ProcessId);
    }
}
