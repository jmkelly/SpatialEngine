using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Spatial.PluginSdk.Http;

namespace Spatial.Host.Tests;

/// <summary>
/// Request-decoding validation in <see cref="Spatial.Host.Api.InvocationRequestBuilder"/>:
/// an explicit provider pin and a resource token are the two routing options a
/// caller can get wrong, and each failure must surface as an actionable HTTP 400
/// (the 4xx-vs-completed split is documented on <c>POST /api/invocations</c>).
/// </summary>
public sealed class InvocationRequestValidationTests : IClassFixture<HostApiTestFactory>
{
    private readonly HostApiTestFactory _factory;

    public InvocationRequestValidationTests(HostApiTestFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task Malformed_provider_id_is_a_bad_request_with_the_expected_format_hint()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.echo@1", Provider: "ghost"),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("'ghost' is not a valid provider id", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Provider_id_with_version_is_accepted_even_when_no_provider_serves()
    {
        // Provider id parses (name@version); unavailability is a completed outcome, not a decode error.
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.echo@1", Provider: "ghost@9"),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.False(body?.Ok);
        Assert.Equal("provider.unavailable", body?.Error?.Code);
    }

    [Fact]
    public async Task Resource_token_that_is_not_a_guid_is_a_bad_request()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.echo@1", Resource: "not-a-token"),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("'not-a-token' is not a live resource token", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Well_formed_but_unknown_resource_token_is_a_bad_request()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.echo@1", Resource: Guid.NewGuid().ToString()),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("is not a live resource token", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Live_resource_token_routes_the_invocation_to_the_resource_owner()
    {
        var token = await MintTokenAsync();

        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.peek@1", Resource: token),
            HostApiJson.Options);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.NotNull(body);
        Assert.True(body.Ok);
        // The resource's owner (fixture@1) serves the call — the resource-local routing proof.
        Assert.Equal("fixture@1", body.Result?.GetValue<string>());
        Assert.Equal("fixture@1", body.Provenance?.Provider);
        Assert.Equal("ResourceLocal", body.Provenance?.Step);
    }

    private async Task<string> MintTokenAsync()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.mint@1"),
            HostApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        return body!.Result!["$resource"]!["token"]!.GetValue<string>();
    }
}
