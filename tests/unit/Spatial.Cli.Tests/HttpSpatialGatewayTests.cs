using System.Net;
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

    [Fact]
    public void Ctor_rejects_a_null_client() =>
        Assert.Throws<ArgumentNullException>(() => new HttpSpatialGateway((HttpClient)null!));

    [Fact]
    public void Ctor_rejects_null_settings() =>
        Assert.Throws<ArgumentNullException>(() => new HttpSpatialGateway((CliSettings)null!));

    [Fact]
    public void Ctor_binds_the_configured_host_without_network_traffic()
    {
        var settings = new CliSettings(
            "http://127.0.0.1:5201", null, "memory", "spatial.json",
            "/arcgis/rest/services", false, false, false, false, TimeSpan.FromSeconds(17));

        using var gateway = new HttpSpatialGateway(settings);
    }

    [Fact]
    public async Task CheckHealth_returns_status_and_stores()
    {
        using var gateway = new HttpSpatialGateway(new HttpClient(
            new StubHealth(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"ready","stores":["memory","demo"]}"""),
            })
        )
        {
            BaseAddress = new Uri("http://127.0.0.1:5201"),
        });

        var health = await gateway.CheckHealthAsync();

        Assert.Equal("ready", health.Status);
        Assert.Equal(["memory", "demo"], health.Stores);
    }

    [Fact]
    public async Task CheckHealth_reports_a_host_that_is_not_ready()
    {
        using var gateway = new HttpSpatialGateway(new HttpClient(
            new StubHealth(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
        )
        {
            BaseAddress = new Uri("http://127.0.0.1:5201"),
        });

        var exception = await Assert.ThrowsAsync<SpatialClientException>(() => gateway.CheckHealthAsync());

        Assert.Equal("store.unavailable", exception.Code);
        Assert.Equal(503, exception.StatusCode);
    }

    private sealed class StubHealth : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHealth(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/health/ready", request.RequestUri!.AbsolutePath);
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class FailsSourceFetch : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException($"Name or service not known ({request.RequestUri!.Host}:443)");
    }
}
