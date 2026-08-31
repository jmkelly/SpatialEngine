using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Http;
using Spatial.Runtime.Capabilities;

namespace Spatial.Host.Tests;

/// <summary>
/// End-to-end tests of the public host API against a real
/// <see cref="Spatial.Host.SpatialHostRuntime"/> wired with the in-memory
/// <see cref="ApiFixtureProvider"/> — the transport-level proof that the
/// independently executable host serves automated clients (plan §16
/// Phase 9). The factory drives the real HTTP pipeline; the runtime is the
/// composition seam (<c>SpatialHostRuntime.For</c>).
/// </summary>
public sealed class HostApiTests : IClassFixture<HostApiTestFactory>
{
    private readonly HostApiTestFactory _factory;

    public HostApiTests(HostApiTestFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client => _factory.CreateClient();

    // ---- Capabilities ----

    [Fact]
    public async Task Capabilities_list_returns_every_registered_capability_with_providers()
    {
        var response = await Client.GetAsync("/api/capabilities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summaries = await response.Content.ReadFromJsonAsync<CapabilitySummaryDto[]>(HostApiJson.Options);
        Assert.NotNull(summaries);
        var echo = Assert.Single(summaries, summary => summary.Id == "fixture.echo@1");
        Assert.Equal("fixture@1", Assert.Single(echo.Providers));
        Assert.Contains("Cancellable", echo.Traits);
        Assert.Single(summaries, summary => summary.Id == "fixture.jobstream@1");
        Assert.Single(summaries, summary => summary.Id == "fixture.sleep@1");
    }

    [Fact]
    public async Task Capability_detail_returns_the_full_contract_declaration()
    {
        var response = await Client.GetAsync("/api/capabilities/fixture.jobstream@1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.Content.ReadFromJsonAsync<CapabilityDetailDto>(HostApiJson.Options);
        Assert.NotNull(detail);
        Assert.Equal("fixture.jobstream@1", detail.Id);
        Assert.Equal("stream.string", detail.OutputSchema);
        Assert.Contains("LongRunning", detail.Traits);
        Assert.Contains("Streaming", detail.Traits);
        Assert.Single(detail.Providers, provider => provider.Id == "fixture@1");
    }

    [Fact]
    public async Task Capability_detail_is_404_for_an_unknown_capability()
    {
        var response = await Client.GetAsync("/api/capabilities/spatial.does.not.exist@1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Invocations: inline ----

    [Fact]
    public async Task Inline_invocation_completes_with_the_wire_encoded_result()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.echo@1", Arguments: new Dictionary<string, JsonNode?>
            {
                ["text"] = "hello world",
            }),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.NotNull(body);
        Assert.Equal("completed", body.Kind);
        Assert.True(body.Ok);
        Assert.Equal("hello world", body.Result?.GetValue<string>());
        Assert.NotNull(body.Provenance);
        Assert.Equal("fixture.echo@1", body.Provenance.Capability);
        Assert.Equal("fixture@1", body.Provenance.Provider);
        Assert.Equal("FirstHealthy", body.Provenance.Step);
    }

    [Fact]
    public async Task Inline_invocation_failure_returns_a_structured_error()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.fail@1"),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.NotNull(body);
        Assert.Equal("completed", body.Kind);
        Assert.False(body.Ok);
        Assert.Equal("provider.failure", body.Error?.Code);
    }

    [Fact]
    public async Task Inline_invocation_reports_permission_denied()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.denied@1", Permissions: new List<string>()),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.NotNull(body);
        Assert.False(body.Ok);
        Assert.Equal("permission.denied", body.Error?.Code);
    }

    [Fact]
    public async Task Inline_invocation_with_permissions_succeeds()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.denied@1", Permissions: new List<string> { "fixture.admin" }),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.True(body?.Ok);
    }

    [Fact]
    public async Task Unresolvable_capability_completes_with_capability_not_found()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("spatial.missing@9"),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.NotNull(body);
        Assert.False(body.Ok);
        Assert.Equal("capability.not.found", body.Error?.Code);
    }

    // ---- Invocations: request validation ----

    [Fact]
    public async Task Malformed_capability_id_is_a_bad_request()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("not-a-capability"),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Undecodable_argument_is_a_bad_request()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.echo@1", Arguments: new Dictionary<string, JsonNode?>
            {
                ["text"] = new JsonObject { ["$i64"] = "not-a-number" },
            }),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Explicit_unknown_provider_completes_with_provider_unavailable()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.echo@1", Provider: "ghost@9"),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.False(body?.Ok);
        Assert.Equal("provider.unavailable", body?.Error?.Code);
    }

    // ---- Invocations: jobs and streams ----

    [Fact]
    public async Task Long_running_invocation_starts_a_job_and_returns_its_location()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.sleep@1", Arguments: new Dictionary<string, JsonNode?>
            {
                ["milliseconds"] = JsonValue.Create(30L),
            }),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.NotNull(body);
        Assert.Equal("job", body.Kind);
        Assert.NotNull(body.Job);
        Assert.Equal("/api/jobs/" + body.Job.JobId, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Streaming_invocation_returns_a_resource_handle_to_read()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.stream@1", Arguments: new Dictionary<string, JsonNode?>
            {
                ["chunks"] = JsonValue.Create(3),
            }),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.NotNull(body);
        Assert.True(body.Ok);
        var resource = body.Result?["$resource"];
        Assert.NotNull(resource);
        Assert.Equal("fixture.stream", resource["kind"]?.GetValue<string>());
        var token = resource["token"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var metadata = await Client.GetAsync($"/api/resources/{token}/metadata");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        var resourceDto = await metadata.Content.ReadFromJsonAsync<ResourceDto>(HostApiJson.Options);
        Assert.Equal("fixture@1", resourceDto?.Owner);
    }
}

/// <summary>
/// One WebApplicationFactory per test class, wired with the in-memory fixture
/// runtime. The default (base-class) factory would run the Program-created
/// runtime with no packages; this override injects the fixture registry
/// through the composition seam before any request is served.
/// </summary>
public sealed class HostApiTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var registry = new CapabilityRegistry();
            registry.Register(new ApiFixtureProvider());
            services.AddSingleton(Spatial.Host.SpatialHostRuntime.For(new CapabilityRuntime(registry)));
        });
    }
}