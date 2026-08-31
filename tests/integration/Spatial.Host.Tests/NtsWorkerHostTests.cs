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
/// The plan's first end-to-end slice over HTTP (plan §17.1–6): the real
/// independently executable host, a real NetTopologySuite worker package
/// activated by the process supervisor, and a buffer invocation served by the
/// isolated worker — through the public API, no in-process plugin reference
/// (the architecture guard requires it). The package is written to a temp
/// tree and the factory boots the host against it.
/// </summary>
public sealed class NtsWorkerHostTests : IDisposable
{
    private readonly string _root;
    private readonly WebApplicationFactory<Program> _factory;

    public NtsWorkerHostTests()
    {
        _root = Directory.CreateTempSubdirectory("spatial-nts-host-").FullName;
        NtsHostPackageWriter.Write(_root);
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("Spatial:PackagesRoot", _root));
    }

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task The_isolated_nts_worker_serves_a_buffer_invocation_over_http()
    {
        // The worker is up: the plugin surface shows it.
        var plugins = await Client.GetFromJsonAsync<PluginDto[]>("/api/plugins", HostApiJson.Options);
        Assert.NotNull(plugins);
        var nts = Assert.Single(plugins, plugin => plugin.Id == "nts@1");
        Assert.Contains("spatial.geometry.buffer@1", nts.Capabilities.Select(capability => capability.Id));

        // The capability is registered from the worker's manifest.
        var capability = await Client.GetFromJsonAsync<CapabilityDetailDto>(
            "/api/capabilities/spatial.geometry.buffer@1", HostApiJson.Options);
        Assert.NotNull(capability);
        Assert.Equal("nts@1", Assert.Single(capability.Providers).Id);

        // Invoke a buffer over HTTP: the point is a codec-encoded geometry.
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
        Assert.Equal("nts@1", body.Provenance?.Provider);

        var buffered = Assert.IsAssignableFrom<IGeometry>(ValueCodec.Decode(body.Result));
        Assert.False(buffered.IsEmpty);
        if (buffered.Envelope is not { } envelope)
        {
            throw new Xunit.Sdk.XunitException("buffer result has an envelope");
        }

        Assert.InRange(envelope.MinX, -1.01, -0.99);
        Assert.InRange(envelope.MaxX, 0.99, 1.01);
        Assert.InRange(envelope.MinY, -1.01, -0.99);
        Assert.InRange(envelope.MaxY, 0.99, 1.01);

        // The ready endpoint reflects the activated plugin.
        var ready = await Client.GetFromJsonAsync<JsonObject>("/health/ready");
        Assert.Equal(1, ready?["plugins"]?.GetValue<int>());
    }

    [Fact]
    public async Task An_unknown_capability_is_a_completed_error_over_http_even_with_workers_loaded()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("spatial.missing@9"),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.False(body?.Ok);
        Assert.Equal("capability.not.found", body?.Error?.Code);
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
