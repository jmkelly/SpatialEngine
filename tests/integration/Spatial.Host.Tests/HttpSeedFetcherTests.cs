using System.Net;
using System.Text;
using Spatial.Contracts;
using Spatial.Host.Api;

namespace Spatial.Host.Tests;

/// <summary>
/// The production seed downloader (ADR-0078): only absolute http(s) URLs are
/// fetched under the ingest byte cap, and every transport failure surfaces as
/// the <see cref="SpatialException"/> the runner records per item.
/// </summary>
public sealed class HttpSeedFetcherTests
{
    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> script) : HttpMessageHandler
    {
        public readonly List<Uri> Requested = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return script(request, cancellationToken);
        }
    }

    private static (HttpSeedFetcher Fetcher, ScriptedHandler Handler) Fetcher(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> script)
    {
        var handler = new ScriptedHandler(script);
        return (new HttpSeedFetcher(new HttpClient(handler)), handler);
    }

    private static Task<HttpResponseMessage> Ok(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

    [Fact]
    public async Task Fetch_returns_the_downloaded_bytes()
    {
        var (fetcher, handler) = Fetcher((_, _) => Ok("{\"type\":\"FeatureCollection\",\"features\":[]}"));

        var bytes = await fetcher.FetchAsync("https://example.test/cities.geojson", 1_000_000, CancellationToken.None);

        Assert.Equal("{\"type\":\"FeatureCollection\",\"features\":[]}", Encoding.UTF8.GetString(bytes));
        Assert.Equal("https://example.test/cities.geojson", handler.Requested.Single().ToString());
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.test/cities.geojson")]
    [InlineData("cities.geojson")]
    [InlineData("")]
    public async Task Fetch_rejects_anything_but_an_absolute_http_url_without_a_request(string url)
    {
        var requested = 0;
        var (fetcher, _) = Fetcher((_, _) =>
        {
            requested++;
            return Ok(string.Empty);
        });

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            fetcher.FetchAsync(url, 1_000_000, CancellationToken.None));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Equal(0, requested);
    }

    [Fact]
    public async Task Fetch_reports_a_missing_source()
    {
        var (fetcher, _) = Fetcher((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            fetcher.FetchAsync("https://example.test/gone.geojson", 1_000_000, CancellationToken.None));

        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [Fact]
    public async Task Fetch_rejects_an_unsuccessful_download()
    {
        var (fetcher, _) = Fetcher((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            fetcher.FetchAsync("https://example.test/cities.geojson", 1_000_000, CancellationToken.None));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("500", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_refuses_a_download_past_the_byte_cap()
    {
        var (fetcher, _) = Fetcher((_, _) => Ok(new string('x', 100)));

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            fetcher.FetchAsync("https://example.test/big.geojson", 10, CancellationToken.None));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("10", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_reports_a_transport_failure()
    {
        var (fetcher, _) = Fetcher((_, _) => throw new HttpRequestException("connection reset"));

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            fetcher.FetchAsync("https://example.test/cities.geojson", 1_000_000, CancellationToken.None));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("connection reset", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_reports_a_timeout()
    {
        var (fetcher, _) = Fetcher((_, _) => throw new TaskCanceledException("the request timed out"));

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            fetcher.FetchAsync("https://example.test/cities.geojson", 1_000_000, CancellationToken.None));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("timed out", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fetch_lets_cancellation_through()
    {
        var (fetcher, _) = Fetcher((_, _) => Ok("late"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fetcher.FetchAsync("https://example.test/cities.geojson", 1_000_000, cancelled.Token));
    }
}
