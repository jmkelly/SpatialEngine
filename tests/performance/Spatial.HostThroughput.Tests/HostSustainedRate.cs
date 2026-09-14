using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit.Abstractions;

namespace Spatial.HostThroughput.Tests;

/// <summary>
/// Sustained-rate confirmation of the T-077 p95 budgets, suite D (T-078,
/// folding T-091): the same query/export paths the burst smoke pins, held
/// at a constant offered rate for a fixed duration via
/// <c>SustainedRunner</c> instead of a fixed-iteration burst. Opt-in
/// (<c>SPATIAL_SUSTAINED=1</c>) and skipped otherwise, so the default gate
/// stays fast and green. Knobs: <c>SPATIAL_SUSTAINED_SECONDS</c> (default
/// 15 locally, 60 in the nightly), <c>SPATIAL_SUSTAINED_RPS_QUERY</c>
/// (default 200) and <c>SPATIAL_SUSTAINED_RPS_EXPORT</c> (default 60).
/// In-process loopback only: no network, demo store only, same as the
/// smoke. Buffer stays burst-only (see <c>tests/performance/NIGHTLY.md</c>).
/// </summary>
public sealed class HostSustainedRate : IClassFixture<WebApplicationFactory<Program>>
{
    /// <summary>Opt-in gate: sustained runs only when this is "1".</summary>
    private const string GateVariable = "SPATIAL_SUSTAINED";

    private const string GateReason =
        "sustained-rate suite is opt-in (SPATIAL_SUSTAINED=1); skipped in the default gate.";

    // Budgets: the T-077 proposed p95 upper bounds (see
    // tests/performance/THROUGHPUT.md). Status RED until the quiet nightly
    // confirms them; this slice must NOT optimise product code to hit them.
    private static readonly TimeSpan QueryP95Budget = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ExportP95Budget = TimeSpan.FromMilliseconds(25);

    /// <summary>Overall timeout for one sustained window; bounds a filtered run.</summary>
    private static readonly TimeSpan WindowTimeout = TimeSpan.FromMinutes(5);

    private readonly WebApplicationFactory<Program> _factory;
    private readonly ITestOutputHelper _output;

    public HostSustainedRate(WebApplicationFactory<Program> factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [SkippableFact]
    public async Task Query_p95_holds_under_sustained_load()
    {
        Skip.If(Environment.GetEnvironmentVariable(GateVariable) != "1", GateReason);

        using var client = _factory.CreateClient();
        const string path = "/arcgis/rest/services/demo/FeatureServer/0/query?where=1%3D1&f=json";

        // Warmup: JIT + query plan + threadpool ramp outside the window.
        await WarmupAsync(token => GetQueryAsync(client, path, token), parallelism: 16, iterations: 32);

        var seconds = DurationSeconds();
        var rps = Rps("SPATIAL_SUSTAINED_RPS_QUERY", 200);
        using var timeout = new CancellationTokenSource(WindowTimeout);
        var result = await SustainedRunner.RunAsync(
            rps, TimeSpan.FromSeconds(seconds),
            token => GetQueryAsync(client, path, token),
            cancellationToken: timeout.Token);

        Report("query", rps, result);
        Assert.True(
            result.P95 <= QueryP95Budget,
            $"query sustained p95 {result.P95.TotalMilliseconds:F1}ms exceeds budget {QueryP95Budget.TotalMilliseconds:F0}ms " +
            $"(p50 {result.P50.TotalMilliseconds:F1}ms, max {result.Max.TotalMilliseconds:F1}ms, " +
            $"offered {result.Offered} completed {result.Completed} dropped {result.Dropped}, {result.AchievedRps:F0} rps).");
    }

    [SkippableFact]
    public async Task Export_p95_holds_under_sustained_load()
    {
        Skip.If(Environment.GetEnvironmentVariable(GateVariable) != "1", GateReason);

        using var client = _factory.CreateClient();
        var body = ExportRequest();

        // Warmup: renderer pipeline + Skia init outside the window.
        await WarmupAsync(token => PostRenderAsync(client, body, token), parallelism: 4, iterations: 8);

        var seconds = DurationSeconds();
        var rps = Rps("SPATIAL_SUSTAINED_RPS_EXPORT", 60);
        using var timeout = new CancellationTokenSource(WindowTimeout);
        var result = await SustainedRunner.RunAsync(
            rps, TimeSpan.FromSeconds(seconds),
            token => PostRenderAsync(client, body, token),
            cancellationToken: timeout.Token);

        Report("export", rps, result);
        Assert.True(
            result.P95 <= ExportP95Budget,
            $"export sustained p95 {result.P95.TotalMilliseconds:F1}ms exceeds budget {ExportP95Budget.TotalMilliseconds:F0}ms " +
            $"(p50 {result.P50.TotalMilliseconds:F1}ms, max {result.Max.TotalMilliseconds:F1}ms, " +
            $"offered {result.Offered} completed {result.Completed} dropped {result.Dropped}, {result.AchievedRps:F0} rps).");
    }

    private static async Task WarmupAsync(Func<CancellationToken, Task> drive, int parallelism, int iterations)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await Parallel.ForEachAsync(
            Enumerable.Range(0, iterations),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = timeout.Token },
            async (_, token) => await drive(token));
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

    private void Report(string name, double offeredRps, SustainedResult result) =>
        _output.WriteLine(
            $"{name} sustained: offered={offeredRps:F0} rps for {result.Elapsed.TotalSeconds:F0}s " +
            $"n={result.Completed} dropped={result.Dropped} " +
            $"p50={result.P50.TotalMilliseconds:F1}ms p95={result.P95.TotalMilliseconds:F1}ms " +
            $"p99={result.P99.TotalMilliseconds:F1}ms max={result.Max.TotalMilliseconds:F1}ms " +
            $"achieved={result.AchievedRps:F0} rps (in-process, no network).");

    private static int DurationSeconds()
    {
        var raw = Environment.GetEnvironmentVariable("SPATIAL_SUSTAINED_SECONDS");
        return int.TryParse(raw, out var seconds) && seconds > 0 ? seconds : 15;
    }

    private static double Rps(string variable, double fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return double.TryParse(raw, out var rps) && rps > 0 ? rps : fallback;
    }

    private sealed record ExportViewport(
        double MinX, double MinY, double MaxX, double MaxY, int Width, int Height, string Crs);

    private sealed record ExportLayer(string Dataset, string? Store = null, string? Filter = null);

    private sealed record ExportBody(
        ExportViewport Viewport, JsonElement Style, IReadOnlyList<ExportLayer> Layers, string Format = "png");
}
