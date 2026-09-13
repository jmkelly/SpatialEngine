using Spatial.Cli;
using Spatial.Client;

namespace Spatial.Cli.Tests;

/// <summary>The gateway's ingest-source handling (ADR-0052).</summary>
public sealed class HttpSpatialGatewayTests
{
    [Fact]
    public async Task Ingest_reports_a_source_fetch_failure_against_the_source()
    {
        using var gateway = new HttpSpatialGateway(new HttpClient(new FailsSourceFetch())
        {
            BaseAddress = new Uri("http://127.0.0.1:5201"),
        });
        var upload = new IngestUpload("x.geojson", "public.x", 4326);

        var exception = await Assert.ThrowsAsync<CliSourceException>(
            () => gateway.IngestAsync("https://example.invalid/x.geojson", upload, "token"));

        Assert.Contains("example.invalid", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Could not reach the host", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FailsSourceFetch : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException($"Name or service not known ({request.RequestUri!.Host}:443)");
    }
}
