using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Spatial.Spike.QueryBaseline;

/// <summary>One timed HTTP call: wall time and the response size in bytes.</summary>
internal readonly record struct HttpSample(double Milliseconds, long Bytes);

/// <summary>One end-to-end Feature Service request: wall time, response bytes and what came back.</summary>
internal sealed record HttpReport(
    string Scenario,
    string Variant,
    int Iterations,
    double P50Milliseconds,
    double P95Milliseconds,
    long ResponseBytes,
    long ResultRows);

/// <summary>
/// The same requests over HTTP against a running host's demo FeatureServer
/// (layer 0 is <c>demo.world_cities</c>). This is the number a client actually
/// sees — it includes the Esri JSON the in-process paths never write — and the
/// <c>countOnly</c> variant doubles as the fidelity check on the in-adapter
/// mirror: the host counts through the real <c>FeatureSpatialMatcher</c>, so
/// its count must equal the mirror's.
/// </summary>
internal static class HttpBaseline
{
    private const string BasePath = "/arcgis/rest/services/demo/FeatureServer";

    /// <summary>The demo layer the world-cities snapshot is published as.</summary>
    private const string LayerName = "world_cities";

    /// <summary>
    /// The layer id the snapshot is published under, found by name so the
    /// number does not depend on the demo catalogue's ordering.
    /// </summary>
    internal static async Task<int> ResolveLayerAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync($"{BasePath}?f=json", cancellationToken));
        foreach (var layer in document.RootElement.GetProperty("layers").EnumerateArray())
        {
            if (string.Equals(layer.GetProperty("name").GetString(), LayerName, StringComparison.OrdinalIgnoreCase))
            {
                return layer.GetProperty("id").GetInt32();
            }
        }

        throw new InvalidOperationException($"the host's demo FeatureServer does not publish a '{LayerName}' layer.");
    }

    /// <summary>One page-walk result: how many round trips a full transfer took, and what it cost.</summary>
    internal sealed record PagingReport(
        string Scenario,
        int Pages,
        long Bytes,
        double TotalMilliseconds,
        double MeanPageMilliseconds,
        int LastPageRows);

    /// <summary>
    /// The paging behaviour: walk the whole matched set with
    /// <c>resultOffset</c> at the layer's <c>maxRecordCount</c> and report the
    /// round trips, bytes and time. Every page is a fresh request, so this is
    /// where a scan-everything store pays its cost once per page.
    /// </summary>
    internal static async Task<IReadOnlyList<PagingReport>> PageThroughAsync(
        string host,
        int warmup,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri(host) };
        var layer = await ResolveLayerAsync(client, cancellationToken);
        var reports = new List<PagingReport>();
        foreach (var request in QueryRequest.Scenarios)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["f"] = "json",
                ["where"] = request.Where,
                ["geometry"] = FormattableString.Invariant($"{request.Bbox.MinX},{request.Bbox.MinY},{request.Bbox.MaxX},{request.Bbox.MaxY}"),
                ["geometryType"] = "esriGeometryEnvelope",
                ["inSR"] = "4326",
                ["spatialRel"] = "esriSpatialRelIntersects",
                ["outFields"] = "*",
                ["orderByFields"] = FormattableString.Invariant($"{request.OrderByField} DESC"),
            };

            for (var i = 0; i < warmup; i++)
            {
                using var warm = await client.GetAsync(Query(layer, parameters, 0), cancellationToken);
            }

            var pages = 0;
            long bytes = 0;
            var rows = 0;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var lastPageRows = 0;
            var exceeded = true;
            while (exceeded)
            {
                var page = await ReadAsync(client, Query(layer, parameters, rows), cancellationToken);
                var carried = CountOf(page);
                lastPageRows = (int)carried;
                bytes += page.RootElement.GetRawText().Length;
                rows += (int)carried;
                pages++;
                exceeded = page.RootElement.TryGetProperty("exceededTransferLimit", out var flag) && flag.GetBoolean();
            }

            var total = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            reports.Add(new PagingReport(request.Scenario, pages, bytes, Math.Round(total, 3), Math.Round(total / pages, 3), lastPageRows));
        }

        return reports;
    }

    private static string Query(int layer, Dictionary<string, string> parameters, int offset)
    {
        var withOffset = new Dictionary<string, string>(parameters, StringComparer.Ordinal)
        {
            ["resultOffset"] = offset.ToString(CultureInfo.InvariantCulture),
        };
        return $"{BasePath}/{layer}/query?{string.Join('&', withOffset.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"))}";
    }

    internal static async Task<IReadOnlyList<HttpReport>> RunAsync(
        string host,
        int warmup,
        int iterations,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri(host) };
        var layer = await ResolveLayerAsync(client, cancellationToken);
        Console.WriteLine($"# end-to-end layer {layer} ({LayerName}) of {BasePath}");
        var reports = new List<HttpReport>(); foreach (var request in QueryRequest.Scenarios)
        {
            foreach (var variant in QueryRequest.Variants)
            {
                var path = Path(layer, request, variant);
                var samples = await SampleAsync(client, path, warmup, iterations, cancellationToken);
                var sorted = samples.Select(sample => sample.Milliseconds).Order().ToArray();
                var body = await ReadAsync(client, path, cancellationToken);
                reports.Add(new HttpReport(
                    request.Scenario,
                    variant,
                    samples.Count,
                    Math.Round(sorted[sorted.Length / 2], 3),
                    Math.Round(sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(0.95 * sorted.Length) - 1)], 3),
                    (long)samples.Average(sample => sample.Bytes),
                    CountOf(body)));
            }
        }

        return reports;
    }

    /// <summary>Warms up, then times <paramref name="iterations"/> calls, recording each response size.</summary>
    internal static async Task<IReadOnlyList<HttpSample>> SampleAsync(
        HttpClient client,
        string path,
        int warmup,
        int iterations,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < warmup; i++)
        {
            using var warm = await client.GetAsync(path, cancellationToken);
        }

        var samples = new List<HttpSample>(iterations);
        for (var i = 0; i < iterations; i++)
        {
            var started = Stopwatch.GetTimestamp();
            using var response = await client.GetAsync(path, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            samples.Add(new HttpSample(Stopwatch.GetElapsedTime(started).TotalMilliseconds, body.LongLength));
        }

        return samples;
    }

    /// <summary>Reads one response document, for the row count.</summary>
    internal static async Task<JsonDocument> ReadAsync(HttpClient client, string path, CancellationToken cancellationToken) =>
        JsonDocument.Parse(await client.GetStringAsync(path, cancellationToken));

    /// <summary>The rows the response carries: a count, the paged features, or the statistics rows.</summary>
    internal static long CountOf(JsonDocument document)
    {
        var root = document.RootElement;
        if (root.TryGetProperty("count", out var count))
        {
            return count.GetInt64();
        }

        return root.TryGetProperty("features", out var features) ? features.GetArrayLength() : 0;
    }

    internal static string Path(int layer, QueryRequest request, string variant)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["f"] = "json",
            ["where"] = request.Where,
            ["geometry"] = FormattableString.Invariant($"{request.Bbox.MinX},{request.Bbox.MinY},{request.Bbox.MaxX},{request.Bbox.MaxY}"),
            ["geometryType"] = "esriGeometryEnvelope",
            ["inSR"] = "4326",
            ["spatialRel"] = "esriSpatialRelIntersects",
            ["outFields"] = "*",
        };

        if (QueryRequest.IsCountOnly(variant))
        {
            parameters["returnCountOnly"] = "true";
            return Query(layer, parameters);
        }

        if (QueryRequest.IsStatistics(variant))
        {
            parameters["outStatistics"] = StatisticsJson(request);
            parameters["groupByFieldsForStatistics"] = request.GroupByField ?? string.Empty;
            return Query(layer, parameters);
        }

        parameters["orderByFields"] = request.OrderDescending
            ? FormattableString.Invariant($"{request.OrderByField} DESC")
            : request.OrderByField;
        parameters["resultRecordCount"] = request.ResultRecordCount.ToString(CultureInfo.InvariantCulture);
        return Query(layer, parameters);
    }

    private static string StatisticsJson(QueryRequest request) =>
        FormattableString.Invariant(
            $$"""[{"statisticType":"count","onStatisticField":"*","outStatisticFieldName":"n"},{"statisticType":"{{QueryRequest.StatisticsType}}","onStatisticField":"{{QueryRequest.PopulationField}}","outStatisticFieldName":"avg_{{QueryRequest.PopulationField}}"}]""");

    private static string Query(int layer, Dictionary<string, string> parameters) =>
        $"{BasePath}/{layer}/query?{string.Join('&', parameters.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"))}";
}
