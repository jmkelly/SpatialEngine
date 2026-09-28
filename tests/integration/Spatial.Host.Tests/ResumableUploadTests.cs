using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// Resumable upload over HTTP (ADR-0090): a large document is staged in
/// chunks, the staging says how far it got, and only a <em>complete</em>
/// staged upload is ever ingested. The dataset appears atomically at the
/// ingest step, so a partial upload cannot be mistaken for a loaded one.
/// </summary>
public sealed class ResumableUploadTests : IDisposable
{
    private const string Token = "test-admin-token";

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-resumable-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private WebApplicationFactory<Program> Factory(long maxBytes = 100_000_000) =>
        new UploadFactory(Path.Combine(_directory, "uploads"), maxBytes);

    private static HttpRequestMessage Authorized(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>A token-carrying read, as every upload route requires one.</summary>
    private static Task<HttpResponseMessage> AuthorizedGetAsync(HttpClient client, string path) =>
        client.SendAsync(Authorized(HttpMethod.Get, path));

    private static ByteArrayContent Chunk(string text) =>
        new ByteArrayContent(Encoding.UTF8.GetBytes(text)) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } };

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A document big enough that chunking is the point, not a detail.</summary>
    private static string Document(int features)
    {
        var rows = new StringBuilder("{\"type\":\"FeatureCollection\",\"features\":[");
        for (var index = 0; index < features; index++)
        {
            rows.Append(index == 0 ? "" : ",")
                .Append("{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[")
                .Append(index)
                .Append(",52.5]},\"properties\":{\"name\":\"p")
                .Append(index)
                .Append("\"}}");
        }

        return rows.Append("]}").ToString();
    }

    /// <summary>Cuts a document at a byte offset, because a chunk boundary is not a feature boundary.</summary>
    private static (string First, string Second) SplitAt(string document, int offset) =>
        (document[..offset], document[offset..]);

    private static async Task<HttpResponseMessage> AppendAsync(
        HttpClient client, string id, long offset, string chunk, long? total = null, string? sha256 = null)
    {
        var query = $"offset={offset}" + (total is null ? "" : $"&total={total}") + (sha256 is null ? "" : $"&sha256={sha256}");
        return await client.SendAsync(Authorized(HttpMethod.Put, $"/api/uploads/{id}?{query}", Chunk(chunk)));
    }

    [Fact]
    public async Task A_chunked_upload_is_ingested_from_the_staged_bytes()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var document = Document(200);
        var (first, second) = SplitAt(document, document.Length / 2);

        var started = await client.SendAsync(Authorized(HttpMethod.Post, "/api/uploads?id=doc1"));
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        Assert.Equal(0, (await BodyAsync(started)).GetProperty("received").GetInt64());

        var midway = await AppendAsync(client, "doc1", 0, first, document.Length, Sha256(document));
        Assert.Equal(first.Length, (await BodyAsync(midway)).GetProperty("received").GetInt64());
        Assert.False((await BodyAsync(midway)).GetProperty("complete").GetBoolean());

        var complete = await AppendAsync(client, "doc1", first.Length, second);
        Assert.True((await BodyAsync(complete)).GetProperty("complete").GetBoolean());

        var ingest = await client.SendAsync(Authorized(
            HttpMethod.Post, "/api/ingest?store=memory&dataset=public.big&srid=4326&format=geojson&upload=doc1"));
        Assert.True(ingest.IsSuccessStatusCode, await ingest.Content.ReadAsStringAsync());
        Assert.Equal(200, (await BodyAsync(ingest)).GetProperty("features").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/datasets/public.big?store=memory")).StatusCode);
    }

    [Fact]
    public async Task A_partial_staged_upload_is_never_ingested_and_no_dataset_appears()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var document = Document(10);
        var (first, second) = SplitAt(document, 100);

        await client.SendAsync(Authorized(HttpMethod.Post, "/api/uploads?id=half"));
        await AppendAsync(client, "half", 0, first, document.Length, Sha256(document));

        var ingest = await client.SendAsync(Authorized(
            HttpMethod.Post, "/api/ingest?store=memory&dataset=public.half&srid=4326&format=geojson&upload=half"));

        Assert.Equal(HttpStatusCode.BadRequest, ingest.StatusCode);
        Assert.Equal("invalid.arguments", (await BodyAsync(ingest)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/datasets/public.half?store=memory")).StatusCode);
    }

    [Fact]
    public async Task A_resumed_upload_continues_from_the_offset_the_host_reports()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var document = Document(50);
        var (first, second) = SplitAt(document, 400);

        await client.SendAsync(Authorized(HttpMethod.Post, "/api/uploads?id=resume"));
        await AppendAsync(client, "resume", 0, first, document.Length, Sha256(document));

        // The client lost its own state, so it asks the host where it got to.
        var described = await AuthorizedGetAsync(client, "/api/uploads/resume");
        var offset = (await BodyAsync(described)).GetProperty("received").GetInt64();
        Assert.Equal(first.Length, offset);

        var finished = await AppendAsync(client, "resume", offset, second);
        Assert.True((await BodyAsync(finished)).GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task A_chunk_appended_at_the_wrong_offset_is_a_bad_request()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        await client.SendAsync(Authorized(HttpMethod.Post, "/api/uploads?id=gap"));

        var response = await AppendAsync(client, "gap", 99, "abc");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid.arguments", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Staging_is_gated_by_the_admin_token_like_every_other_mutation()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var start = await client.PostAsync("/api/uploads?id=nope", null);
        var describe = await client.GetAsync("/api/uploads/nope");

        Assert.Equal(HttpStatusCode.Unauthorized, start.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, describe.StatusCode);
    }

    [Fact]
    public async Task An_unknown_upload_is_not_found()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var described = await AuthorizedGetAsync(client, "/api/uploads/ghost");
        var ingested = await client.SendAsync(Authorized(
            HttpMethod.Post, "/api/ingest?store=memory&dataset=public.ghost&srid=4326&format=geojson&upload=ghost"));

        Assert.Equal(HttpStatusCode.NotFound, described.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ingested.StatusCode);
    }

    [Fact]
    public async Task Staged_bytes_are_bounded_by_the_configured_byte_cap()
    {
        using var factory = Factory(maxBytes: 64);
        var client = factory.CreateClient();
        await client.SendAsync(Authorized(HttpMethod.Post, "/api/uploads?id=big"));

        var response = await AppendAsync(client, "big", 0, new string('x', 65));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid.arguments", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_digest_that_does_not_match_never_completes_the_upload()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        await client.SendAsync(Authorized(HttpMethod.Post, "/api/uploads?id=digest&total=3&sha256=" + Sha256("abc")));

        var response = await AppendAsync(client, "digest", 0, "xyz", 3);
        var state = await BodyAsync(await AuthorizedGetAsync(client, "/api/uploads/digest"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(state.GetProperty("complete").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(state.GetProperty("fault").GetString()));
    }

    [Fact]
    public async Task A_malformed_row_in_the_last_chunk_fails_the_whole_ingest_and_leaves_no_dataset()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        const string truncated = """{"type":"FeatureCollection","features":[{"type":"Feature","geom""";

        await client.SendAsync(Authorized(HttpMethod.Post, "/api/uploads?id=broken"));
        await AppendAsync(client, "broken", 0, truncated, truncated.Length, Sha256(truncated));

        var ingest = await client.SendAsync(Authorized(
            HttpMethod.Post, "/api/ingest?store=memory&dataset=public.broken&srid=4326&format=geojson&upload=broken"));

        // The staged bytes survive the failure, so the caller can see and fix
        // the document, but nothing was loaded: the load is still one
        // transaction over the whole staged upload.
        Assert.Equal(HttpStatusCode.BadRequest, ingest.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/datasets/public.broken?store=memory")).StatusCode);
        var state = await BodyAsync(await AuthorizedGetAsync(client, "/api/uploads/broken"));
        Assert.True(state.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task An_ingested_upload_is_discarded_so_its_bytes_cannot_be_loaded_twice()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        const string document = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[13.4,52.5]},"properties":{"name":"Berlin"}}]}
            """;

        await client.SendAsync(Authorized(HttpMethod.Post, "/api/uploads?id=once"));
        await AppendAsync(client, "once", 0, document, document.Length, Sha256(document));
        var query = "/api/ingest?store=memory&dataset=public.once&srid=4326&format=geojson&upload=once";

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Authorized(HttpMethod.Post, query))).StatusCode);
        var again = await client.SendAsync(Authorized(HttpMethod.Post, query));

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task A_body_and_an_upload_together_are_rejected_as_ambiguous()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        const string document = """{"type":"FeatureCollection","features":[]}""";
        await client.SendAsync(Authorized(HttpMethod.Post, "/api/uploads?id=both"));
        await AppendAsync(client, "both", 0, document, document.Length, Sha256(document));

        var response = await client.SendAsync(Authorized(
            HttpMethod.Post,
            "/api/ingest?store=memory&dataset=public.both&srid=4326&format=geojson&upload=both",
            new StringContent("[]", Encoding.UTF8, "application/json")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Staged_uploads_are_listed_and_can_be_discarded()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        await client.SendAsync(Authorized(HttpMethod.Post, "/api/uploads?id=listed"));

        var listed = await BodyAsync(await AuthorizedGetAsync(client, "/api/uploads"));
        Assert.Contains(listed.EnumerateArray(), state => state.GetProperty("uploadId").GetString() == "listed");

        var discarded = await client.SendAsync(Authorized(HttpMethod.Delete, "/api/uploads/listed"));
        Assert.Equal(HttpStatusCode.OK, discarded.StatusCode);
        Assert.True((await BodyAsync(discarded)).GetBoolean());
        Assert.Empty((await BodyAsync(await AuthorizedGetAsync(client, "/api/uploads"))).EnumerateArray());
    }

    private sealed class UploadFactory(string uploadsPath, long maxBytes) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", uploadsPath + ".maps.json");
            builder.UseSetting("Spatial:Uploads:Path", uploadsPath);
            builder.UseSetting("Spatial:Ingest:MaxBytes", maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
