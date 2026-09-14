using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Spatial.Client;
using Spatial.Core.Geometry;
using Xunit.Abstractions;

namespace Spatial.HostThroughput.Tests;

/// <summary>
/// Host throughput smoke, suite C (T-077): p95 latency budgets for the three
/// hot host paths — FeatureServer query, PNG export (<c>POST /api/render</c>)
/// and geometry buffer — driven in-process via <c>WebApplicationFactory</c>
/// (no network, demo store only).
///
/// The smoke is opt-in (<c>SPATIAL_THROUGHPUT=1</c>) and skips otherwise, so
/// the default gate (<c>eng/verify.sh</c>) stays fast and green exactly like
/// the BenchmarkDotNet slice, which builds but never executes under
/// <c>dotnet test</c>. Live Esri latency is recorded as reference only and
/// is never gated: that test asserts no budget and skips when the public
/// service is unreachable.
///
/// Long-running work stays a cancellable <c>Task</c>: every request takes the
/// per-run <c>CancellationToken</c> (overall timeout below), and host-side
/// failures surface as the structured <c>SpatialException</c> codes
/// (<c>invalid.arguments</c>, <c>not.found</c>, <c>store.unavailable</c>)
/// asserted by the smoke's status checks, never as bare 500s.
/// </summary>
public sealed class HostThroughputSmoke : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>Opt-in gate: the smoke runs only when this is "1".</summary>
    private const string GateVariable = "SPATIAL_THROUGHPUT";

    private const string GateReason =
        "throughput smoke is opt-in (SPATIAL_THROUGHPUT=1); skipped in the default gate.";

    /// <summary>Offered concurrency for the measured window (rps proxy, in-process).</summary>
    private const int OfferedConcurrency = 16;

    /// <summary>Overall timeout for one measured window; keeps a filtered run under 2 minutes.</summary>
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromSeconds(100);

    // Budgets: proposed p95 upper bounds from the short-run RED pass
    // (2026-09-14, in-process loopback; see tests/performance/THROUGHPUT.md).
    // Status RED: single-machine short-run samples with ~2x headroom over
    // the observed p95; a full nightly run confirms or revises them. This
    // slice must NOT optimise product code to hit budgets (record deltas,
    // file follow-ups: T-091 nightly driver + confirmation, T-092 tail
    // diagnosis).
    private static readonly TimeSpan QueryP95Budget = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ExportP95Budget = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan BufferP95Budget = TimeSpan.FromMilliseconds(30);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public HostThroughputSmoke(WebApplicationFactory<Program> factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Query_p95_stays_within_budget()
    {
        Skip.If(Environment.GetEnvironmentVariable(GateVariable) != "1", GateReason);

        using var client = _factory.CreateClient();
        const string path = "/arcgis/rest/services/demo/FeatureServer/0/query?where=1%3D1&f=json";

        // Warmup: JIT + query plan + threadpool ramp outside the measured
        // window. The warmup mirrors the measured burst so the p95 reflects
        // steady state, not cold-threadpool ramp.
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 32),
            new ParallelOptions { MaxDegreeOfParallelism = OfferedConcurrency },
            async (_, token) => await GetQueryAsync(client, path, token));

        var result = await MeasureAsync(
            iterations: 300,
            maxParallelism: OfferedConcurrency,
            timeout: WindowTimeout,
            async (token) => await GetQueryAsync(client, path, token),
            budgetMs: QueryP95Budget.TotalMilliseconds);

        Report("query", result);
        Assert.True(
            result.P95 <= QueryP95Budget,
            $"query p95 {result.P95.TotalMilliseconds:F1}ms exceeds budget {QueryP95Budget.TotalMilliseconds:F0}ms " +
            $"(p50 {result.P50.TotalMilliseconds:F1}ms, max {result.Max.TotalMilliseconds:F1}ms, {result.AchievedRps:F0} rps).");
    }

    [SkippableFact]
    public async Task Export_p95_stays_within_budget()
    {
        Skip.If(Environment.GetEnvironmentVariable(GateVariable) != "1", GateReason);

        using var client = _factory.CreateClient();
        var body = ExportRequest();

        // Warmup: renderer pipeline + Skia init outside the measured window.
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 8),
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (_, token) => await PostRenderAsync(client, body, token));

        var result = await MeasureAsync(
            iterations: 100,
            maxParallelism: 4,
            timeout: WindowTimeout,
            async (token) => await PostRenderAsync(client, body, token),
            budgetMs: ExportP95Budget.TotalMilliseconds);

        Report("export", result);
        Assert.True(
            result.P95 <= ExportP95Budget,
            $"export p95 {result.P95.TotalMilliseconds:F1}ms exceeds budget {ExportP95Budget.TotalMilliseconds:F0}ms " +
            $"(p50 {result.P50.TotalMilliseconds:F1}ms, max {result.Max.TotalMilliseconds:F1}ms, {result.AchievedRps:F0} rps).");
    }

    [SkippableFact]
    public async Task Buffer_p95_stays_within_budget()
    {
        Skip.If(Environment.GetEnvironmentVariable(GateVariable) != "1", GateReason);

        var client = new SpatialClient(_factory.CreateClient());
        var point = GeometryFactory.CreatePoint(0, 0);

        // Warmup: codec + operations pipeline + threadpool ramp outside the
        // measured window; mirrors the measured burst (see query warmup).
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 32),
            new ParallelOptions { MaxDegreeOfParallelism = OfferedConcurrency },
            async (_, token) => await client.BufferAsync(point, 1.0, cancellationToken: token));

        var result = await MeasureAsync(
            iterations: 300,
            maxParallelism: OfferedConcurrency,
            timeout: WindowTimeout,
            async (token) => await client.BufferAsync(point, 1.0, cancellationToken: token),
            budgetMs: BufferP95Budget.TotalMilliseconds);

        Report("buffer", result);
        Assert.True(
            result.P95 <= BufferP95Budget,
            $"buffer p95 {result.P95.TotalMilliseconds:F1}ms exceeds budget {BufferP95Budget.TotalMilliseconds:F0}ms " +
            $"(p50 {result.P50.TotalMilliseconds:F1}ms, max {result.Max.TotalMilliseconds:F1}ms, {result.AchievedRps:F0} rps).");
    }

    /// <summary>
    /// Live Esri latency, reference only: recorded to the test log, never
    /// gated. Skips (does not fail) when the public service is unreachable,
    /// so airplane-mode and CI-without-egress runs stay green.
    /// </summary>
    [SkippableFact]
    public async Task Live_esri_latency_is_recorded_as_reference_only()
    {
        Skip.If(Environment.GetEnvironmentVariable(GateVariable) != "1", GateReason);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        const string url = "https://sampleserver6.arcgisonline.com/arcgis/rest/services?f=json";

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        string? skipReason = null;
        try
        {
            response = await client.GetAsync(url);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            skipReason = $"live Esri unreachable ({exception.GetType().Name}); reference latency not recorded.";
        }

        stopwatch.Stop();
        Skip.If(skipReason is not null, skipReason ?? "live Esri unreachable.");
        _output.WriteLine($"live-esri: GET {url} -> {(int)response!.StatusCode} in {stopwatch.Elapsed.TotalMilliseconds:F1}ms (reference only, never gated).");
    }

    private static async Task GetQueryAsync(HttpClient client, string path, CancellationToken token)
    {
        using var response = await client.GetAsync(path, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        Assert.True(document.RootElement.TryGetProperty("features", out _));
    }

    private static async Task PostRenderAsync(HttpClient client, ExportBody body, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("/api/render", body, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        Assert.True(bytes.Length > 8);
    }

    private static ExportBody ExportRequest() => new(
        new ExportViewport(MinX: -10, MinY: 35, MaxX: 30, MaxY: 60, Width: 200, Height: 125, Crs: "EPSG:4326"),
        JsonDocument.Parse(
            """
            { "version": 8, "layers": [
                { "id": "bg", "type": "background", "paint": { "background-color": "#101820" } },
                { "id": "cities", "type": "circle", "source-layer": "demo.cities",
                  "paint": { "circle-color": "#ffd166", "circle-radius": 6 } } ] }
            """).RootElement.Clone(),
        [new ExportLayer(Dataset: "demo.cities", Store: "demo")],
        Format: "png");

    private void Report(string name, ThroughputResult result) =>
        _output.WriteLine(
            $"{name}: n={result.Samples} concurrency={result.Concurrency} " +
            $"p50={result.P50.TotalMilliseconds:F1}ms p95={result.P95.TotalMilliseconds:F1}ms " +
            $"p99={result.P99.TotalMilliseconds:F1}ms max={result.Max.TotalMilliseconds:F1}ms " +
            $"over-budget={result.OverBudget} achieved={result.AchievedRps:F0} rps (in-process, no network).");

    private static async Task<ThroughputResult> MeasureAsync(
        int iterations,
        int maxParallelism,
        TimeSpan timeout,
        Func<CancellationToken, Task> drive,
        double? budgetMs = null)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        var token = timeoutSource.Token;
        var latencies = new double[iterations];
        var wallClock = Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, iterations),
            new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = token },
            async (index, innerToken) =>
            {
                var operation = Stopwatch.StartNew();
                await drive(innerToken);
                operation.Stop();
                latencies[index] = operation.Elapsed.TotalMilliseconds;
            });

        wallClock.Stop();
        Array.Sort(latencies);
        var overBudget = budgetMs.HasValue ? latencies.Count(sample => sample > budgetMs.Value) : 0;
        return new ThroughputResult(
            Samples: iterations,
            Concurrency: maxParallelism,
            P50: TimeSpan.FromMilliseconds(Percentile(latencies, 0.50)),
            P95: TimeSpan.FromMilliseconds(Percentile(latencies, 0.95)),
            P99: TimeSpan.FromMilliseconds(Percentile(latencies, 0.99)),
            Max: TimeSpan.FromMilliseconds(latencies[^1]),
            OverBudget: overBudget,
            AchievedRps: iterations / wallClock.Elapsed.TotalSeconds);
    }

    private static double Percentile(double[] sorted, double quantile)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var rank = Math.Min(sorted.Length - 1, (int)Math.Ceiling(quantile * sorted.Length) - 1);
        return sorted[Math.Max(0, rank)];
    }

    private sealed record ThroughputResult(
        int Samples,
        int Concurrency,
        TimeSpan P50,
        TimeSpan P95,
        TimeSpan P99,
        TimeSpan Max,
        int OverBudget,
        double AchievedRps);

    private sealed record ExportViewport(
        double MinX, double MinY, double MaxX, double MaxY, int Width, int Height, string Crs);

    private sealed record ExportLayer(string Dataset, string? Store = null, string? Filter = null);

    private sealed record ExportBody(
        ExportViewport Viewport, JsonElement Style, IReadOnlyList<ExportLayer> Layers, string Format = "png");
}
