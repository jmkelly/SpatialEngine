using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Spatial.PluginSdk.Codec;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Resources;

namespace Spatial.Host.Tests;

/// <summary>
/// The resource surface (ADR-0022/0023): opaque handles with metadata and
/// disposal, and the NDJSON stream read that carries streaming results to
/// clients — string items, canonical binary (<c>$bytes</c>) items, and the
/// structured <c>$error</c> line a failed stream ends with.
/// </summary>
public sealed class HostResourceTests : IClassFixture<HostApiTestFactory>
{
    private readonly HostApiTestFactory _factory;

    public HostResourceTests(HostApiTestFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task Resource_metadata_and_delete_round_trip()
    {
        var token = await MintTokenAsync();

        var metadata = await Client.GetAsync($"/api/resources/{token}/metadata");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        var resource = await metadata.Content.ReadFromJsonAsync<ResourceDto>(HostApiJson.Options);
        Assert.Equal("fixture.handle", resource?.Kind);
        Assert.Equal("fixture@1", resource?.Owner);
        Assert.Equal(ResourceState.Open, resource?.State);

        var deleted = await Client.DeleteAsync($"/api/resources/{token}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var after = await Client.GetAsync($"/api/resources/{token}/metadata");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Stream_returns_each_string_item_as_an_ndjson_line()
    {
        var token = await StreamingTokenAsync("fixture.stream@1", chunks: 5);

        var response = await Client.GetAsync($"/api/resources/{token}/stream");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);

        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["\"chunk 1\"", "\"chunk 2\"", "\"chunk 3\"", "\"chunk 4\"", "\"chunk 5\""], lines);

        // Consuming a stream to its end closes the resource.
        var after = await Client.GetAsync($"/api/resources/{token}/metadata");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Binary_items_cross_as_bytes_tags_and_decode_back()
    {
        var token = await StreamingTokenAsync("fixture.bytes@1", chunks: 2);

        var response = await Client.GetAsync($"/api/resources/{token}/stream");
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var first = JsonNode.Parse(lines[0]);
        Assert.NotNull(first?["$bytes"]);
        var decoded = Assert.IsType<byte[]>(ValueCodec.Decode(first));
        Assert.Equal(new byte[] { 1, 1 }, decoded);

        var second = Assert.IsType<byte[]>(ValueCodec.Decode(JsonNode.Parse(lines[1])));
        Assert.Equal(new byte[] { 2, 2 }, second);
    }

    [Fact]
    public async Task A_failed_stream_ends_with_a_structured_error_line()
    {
        var token = await StreamingTokenAsync("fixture.failingstream@1", chunks: 0);

        var response = await Client.GetAsync($"/api/resources/{token}/stream");
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("\"only item\"", lines[0]);
        var errorLine = JsonNode.Parse(lines[1]);
        Assert.Equal("provider.failure", errorLine?["$error"]?["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task Job_published_streams_are_readable_while_the_job_runs()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("fixture.jobstream@1", Arguments: new Dictionary<string, JsonNode?>
            {
                ["chunks"] = JsonValue.Create(4),
                ["delayMilliseconds"] = JsonValue.Create(30),
            }),
            HostApiJson.Options);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.Equal("job", body?.Kind);
        var jobId = body!.Job!.JobId;

        // The stream handle is published on the job event log while it runs.
        var token = await PollForPublishedResourceAsync(jobId);
        Assert.False(string.IsNullOrWhiteSpace(token));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/resources/{token}/stream");
        using var streamResponse = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, streamResponse.StatusCode);
        var lines = (await streamResponse.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.Equal("\"chunk 1\"", lines[0]);
        Assert.Equal("\"chunk 4\"", lines[^1]);
    }

    [Fact]
    public async Task Unknown_stream_resource_is_404()
    {
        var response = await Client.GetAsync("/api/resources/00000000000000000000000000000000/stream");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_non_stream_resource_cannot_be_read_as_a_stream()
    {
        var token = await MintTokenAsync();

        var response = await Client.GetAsync($"/api/resources/{token}/stream");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    private async Task<string> StreamingTokenAsync(string capability, int chunks)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest(capability, Arguments: new Dictionary<string, JsonNode?>
            {
                ["chunks"] = JsonValue.Create(chunks),
            }),
            HostApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.True(body?.Ok);
        var token = body!.Result!["$resource"]!["token"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(token));
        return token;
    }

    private async Task<string> PollForPublishedResourceAsync(string jobId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var jobResponse = await Client.GetAsync($"/api/jobs/{jobId}/events");
            var events = await jobResponse.Content.ReadFromJsonAsync<JobEventsResponse>(HostApiJson.Options);
            var published = events?.Events.FirstOrDefault(jobEvent => jobEvent.Resource is not null)?.Resource;
            if (published is not null)
            {
                return published.Id;
            }

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException($"job {jobId} never published a stream resource");
    }
}