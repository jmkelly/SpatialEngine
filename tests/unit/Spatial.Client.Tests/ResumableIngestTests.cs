using System.Net;
using System.Text;
using Spatial.Client;
using Spatial.Contracts.Providers;

namespace Spatial.Client.Tests;

/// <summary>
/// The resumable upload driver (ADR-0089): a caller that uploads a large
/// document in chunks, asks the host where it got to, continues from there and
/// only ingests once the staged upload is complete. The chunk boundaries fall
/// where the client puts them, never where a feature does.
/// </summary>
public sealed class ResumableIngestTests
{
    private const string Document = "the quick brown fox jumps over the lazy dog, twice over";

    private static IngestUpload Upload() => new("big.geojson", "public.big", 4326);

    /// <summary>
    /// Answers the staging protocol, recording the offset every accepted chunk
    /// was appended at. <paramref name="refuseAttempt"/> fails the nth
    /// <c>PUT</c> (1-based), which is how a transport failure mid-upload is
    /// simulated.
    /// </summary>
    private static StubHttpHandler Staging(long total, string digest, int refuseAttempt = 0, List<long>? appended = null)
    {
        long received = 0;
        var puts = 0;

        return new StubHttpHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/uploads" && request.Method == HttpMethod.Post)
            {
                return StubHttpHandler.Json(State("doc", received, total, digest));
            }

            if (path == "/api/uploads/doc" && request.Method == HttpMethod.Get)
            {
                return StubHttpHandler.Json(State("doc", received, total, digest));
            }

            if (path == "/api/uploads/doc" && request.Method == HttpMethod.Put)
            {
                puts++;
                if (puts == refuseAttempt)
                {
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }

                var length = request.Content!.ReadAsByteArrayAsync().Result.Length;
                appended?.Add(received);
                received += length;
                return StubHttpHandler.Json(State("doc", received, total, digest));
            }

            if (path == "/api/ingest")
            {
                return StubHttpHandler.Json("""{"dataset":"public.big","features":2,"srid":4326}""");
            }

            return StubHttpHandler.Json("""{"code":"not.found","message":"no such route"}""", HttpStatusCode.NotFound);
        });
    }

    private static string State(string id, long received, long total, string digest) =>
        $$"""{"uploadId":"{{id}}","received":{{received}},"totalBytes":{{total}},"complete":{{(received >= total ? "true" : "false")}},"sha256":"{{digest}}"}""";

    [Fact]
    public async Task A_document_is_staged_in_chunks_and_ingested_once()
    {
        using var stub = new StubClient(Staging(Document.Length, "digest"));
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(Document));

        var outcome = await ResumableIngest.UploadAsync(
            stub.Client, content, Upload(), new ResumableUpload(ChunkSize: 20, UploadId: "doc"), "admin");

        Assert.Equal("public.big", outcome.Dataset);
        var paths = stub.Handler.Exchanges.Select(exchange => $"{exchange.Request.Method} {exchange.Request.RequestUri!.AbsolutePath}").ToArray();
        Assert.Equal(
            [
                "POST /api/uploads",
                "GET /api/uploads/doc",
                "PUT /api/uploads/doc",
                "PUT /api/uploads/doc",
                "PUT /api/uploads/doc",
                "POST /api/ingest",
            ],
            paths);
        Assert.Contains("upload=doc", stub.Handler.Exchanges[^1].Request.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_upload_resumes_from_the_offset_the_host_reports()
    {
        // The host already holds the first 20 bytes of the document; a client
        // that starts from zero would only be told it is behind.
        var appended = new List<long>();
        long total = Document.Length;
        var handler = new StubHttpHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/uploads/doc" && request.Method == HttpMethod.Get)
            {
                return StubHttpHandler.Json(State("doc", 20, total, "digest"));
            }

            if (path == "/api/uploads/doc" && request.Method == HttpMethod.Put)
            {
                appended.Add(request.Content!.ReadAsByteArrayAsync().Result.Length);
                return StubHttpHandler.Json(State("doc", total, total, "digest"));
            }

            return path == "/api/ingest"
                ? StubHttpHandler.Json("""{"dataset":"public.big","features":2,"srid":4326}""")
                : StubHttpHandler.Json(State("doc", 20, total, "digest"));
        });

        using var stub = new StubClient(handler);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(Document));

        await ResumableIngest.UploadAsync(
            stub.Client, content, Upload(), new ResumableUpload(ChunkSize: 64, UploadId: "doc"), "admin");

        // The first twenty characters are never re-sent: the chunk starts at
        // the host's offset, not the client's.
        Assert.Equal([total - 20], appended);
    }

    [Fact]
    public async Task A_transport_failure_mid_upload_is_retried_from_the_reported_offset()
    {
        var appended = new List<long>();
        var handler = Staging(Document.Length, "digest", refuseAttempt: 1, appended);
        using var stub = new StubClient(handler);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes(Document));

        await ResumableIngest.UploadAsync(
            stub.Client, content, Upload(), new ResumableUpload(ChunkSize: 20, UploadId: "doc", MaxAttempts: 3), "admin");

        // The first chunk was refused in transit; the retry re-asked where the
        // host got to — still nothing staged — and sent the same 20 bytes
        // again, so no byte was lost and none was staged twice.
        Assert.Equal([0, 20, 40], appended);
    }

    [Fact]
    public async Task A_stream_that_cannot_be_rewound_is_rejected_before_anything_is_staged()
    {
        using var stub = new StubClient(Staging(Document.Length, "digest"));
        await using var content = new NonSeekableStream("abc");

        var failure = await Assert.ThrowsAsync<ArgumentException>(() => ResumableIngest.UploadAsync(
            stub.Client, content, Upload(), new ResumableUpload(), "admin"));

        Assert.Contains("seekable", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(stub.Handler.Exchanges);
    }

    /// <summary>A forward-only stream, which is what a live request body looks like.</summary>
    private sealed class NonSeekableStream(string text) : Stream
    {
        private readonly MemoryStream _inner = new(Encoding.UTF8.GetBytes(text));

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
