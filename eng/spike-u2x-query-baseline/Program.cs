using System.Globalization;
using System.Text.Json;
using Spatial.Contracts;
using Spatial.Esri.Codec;

namespace Spatial.Spike.QueryBaseline;

/// <summary>
/// The SpatialEngine-u2x.1 spike: times the feature-query paths on the
/// 34,135-row world-cities layer and prints the numbers. It measures only —
/// nothing in the product is changed, and the rows come from the committed
/// snapshot the demo store already serves.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            await RunAsync(options, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or SpatialException)
        {
            Console.Error.WriteLine($"the spike could not run: {exception}");
            return 1;
        }
    }

    private static async Task RunAsync(Options options, CancellationToken cancellationToken)
    {
        var loaded = await DatasetLoader.PrepareAsync(options.Store, options.Connection, options.Reuse, cancellationToken);
        var model = LayerModel.For(loaded.Description);
        var runner = new QueryRunner(loaded.Store, loaded.Lookup, model, loaded.Resident);

        Console.WriteLine($"# SpatialEngine-u2x.1 query baseline — store={options.Store} label={options.Label}");
        Console.WriteLine($"# layer {loaded.Description.Id} rows={loaded.Resident.Count} srid={loaded.Description.Srid} " +
            $"geometry={loaded.Description.GeometryColumn} idColumns=[{string.Join(',', loaded.Description.IdColumns)}] " +
            $"iterations={options.Iterations} warmup={options.Warmup}");
        Console.WriteLine($"# A=ScanAsync+in-adapter  B=QueryAsync pushdown  C=IFeatureLookup by identity  D=emulated full pushdown (ceiling, not shipped)");
        Console.WriteLine();
        Console.WriteLine(SampleReport.Header);

        var reports = new List<SampleReport>();
        foreach (var request in QueryRequest.Scenarios)
        {
            var where = request.ParsedWhere();
            var attributePushdown = await runner.SupportsAttributePushdownAsync(request, cancellationToken);
            var storeFilter = attributePushdown ? request.StoreFilter : null;
            Console.WriteLine(
                $"# {request.Scenario}: bbox {request.Bbox} where '{request.Where}' " +
                $"→ attribute pushdown {(attributePushdown ? "supported" : "NOT supported by this store (bbox only)")}");
            foreach (var variant in QueryRequest.Variants)
            {
                reports.Add(await MeasureAsync(options, "A-scan", request, variant, where, token => runner.ScanAsync(request, variant, where, token), cancellationToken));
                reports.Add(await MeasureAsync(options, attributePushdown ? "B-push" : "B-bbox", request, variant, where, Pushdown(runner, request, variant, where, storeFilter), cancellationToken));
                reports.Add(await MeasureAsync(options, "D-emul", request, variant, where, Emulated(runner, request, variant, where), cancellationToken));
                if (!QueryRequest.IsCountOnly(variant) && !QueryRequest.IsStatistics(variant))
                {
                    reports.Add(await MeasureAsync(options, "C-lookup", request, variant, where, token => runner.IdentityAsync(request, token), cancellationToken));
                }
            }
        }

        foreach (var report in reports)
        {
            Console.WriteLine(report.ToRow());
        }

        if (options.Host is { } host)
        {
            await ReportHttpAsync(host, options, reports, cancellationToken);
        }

        if (options.Json is { } json)
        {
            await File.WriteAllTextAsync(json, JsonSerializer.Serialize(new { options.Label, options.Store, reports }), cancellationToken);
            Console.WriteLine();
            Console.WriteLine($"# json written to {json}");
        }
    }

    private static Func<CancellationToken, Task<Outcome>> Pushdown(
        QueryRunner runner,
        QueryRequest request,
        string variant,
        EsriFilterClause? where,
        string? storeFilter) =>
        (token) => runner.PushdownAsync(request, variant, where, storeFilter, token);

    private static Func<CancellationToken, Task<Outcome>> Emulated(
        QueryRunner runner,
        QueryRequest request,
        string variant,
        EsriFilterClause? where) =>
        (token) => runner.EmulatedPushdownAsync(request, variant, where, token);

    private static async Task<SampleReport> MeasureAsync(
        Options options,
        string path,
        QueryRequest request,
        string variant,
        EsriFilterClause? where,
        Func<CancellationToken, Task<Outcome>> operation,
        CancellationToken cancellationToken)
    {
        var samples = await Measurement.RunAsync(() => operation(cancellationToken), options.Warmup, options.Iterations, cancellationToken);
        return SampleReport.From(path, request.Scenario, variant, samples);
    }

    /// <summary>
    /// The end-to-end numbers, and the fidelity check: the host's
    /// <c>returnCountOnly</c> answer comes from the real matcher, so it must
    /// equal the in-adapter mirror's count for the same scenario.
    /// </summary>
    private static async Task ReportHttpAsync(
        string host,
        Options options,
        IReadOnlyList<SampleReport> reports,
        CancellationToken cancellationToken)
    {
        var http = await HttpBaseline.RunAsync(host, options.Warmup, options.Iterations, cancellationToken);
        Console.WriteLine();
        Console.WriteLine($"# end-to-end over HTTP at {host} (demo FeatureServer, {options.Iterations} iterations)");
        Console.WriteLine("scenario variant     p50_ms     p95_ms     bytes    rows");
        foreach (var report in http)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{report.Scenario,-8} {report.Variant,-10} {report.P50Milliseconds,10:F3} {report.P95Milliseconds,10:F3} " +
                $"{report.ResponseBytes,10} {report.ResultRows,10}"));
        }

        Console.WriteLine();
        foreach (var report in http.Where(report => report.Variant == "countOnly"))
        {
            var mirrored = reports.First(candidate =>
                candidate.Scenario == report.Scenario && candidate.Path == "A-scan" && candidate.Variant == "countOnly");
            var agrees = mirrored.ResultRows == report.ResultRows
                ? "MATCH"
                : $"MISMATCH (mirror {mirrored.ResultRows}, host {report.ResultRows})";
            Console.WriteLine($"# mirror fidelity, {report.Scenario} countOnly: {agrees}");
        }

        var paging = await HttpBaseline.PageThroughAsync(host, options.Warmup, cancellationToken);
        Console.WriteLine();
        Console.WriteLine("# paging the whole matched set with resultOffset (the layer's maxRecordCount page)");
        Console.WriteLine("scenario     pages      bytes   total_ms  mean_page_ms  last_page");
        foreach (var report in paging)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{report.Scenario,-8} {report.Pages,8} {report.Bytes,10} {report.TotalMilliseconds,10:F3} " +
                $"{report.MeanPageMilliseconds,12:F3} {report.LastPageRows,11}"));
        }
    }
}

/// <summary>The spike's command line: which store, how many iterations, and where to write the JSON.</summary>
internal sealed record Options(
    string Store,
    string? Connection,
    bool Reuse,
    int Iterations,
    int Warmup,
    string Label,
    string? Host,
    string? Json)
{
    private const string ConnectionVariable = "SPATIAL_POSTGIS_CONNECTION";

    internal static Options Parse(IReadOnlyList<string> args)
    {
        string store = "memory";
        string? connection = null;
        string? host = null;
        string? json = null;
        var label = "default";
        var reuse = false;
        var iterations = 15;
        var warmup = 3;
        foreach (var arg in args)
        {
            var (key, value) = Split(arg);
            switch (key)
            {
                case "--store": store = value ?? "memory"; break;
                case "--connection": connection = value; break;
                case "--reuse": reuse = true; break;
                case "--iterations": iterations = int.Parse(value ?? "15", CultureInfo.InvariantCulture); break;
                case "--warmup": warmup = int.Parse(value ?? "3", CultureInfo.InvariantCulture); break;
                case "--label": label = value ?? "default"; break;
                case "--host": host = value; break;
                case "--json": json = value; break;
                default: throw new ArgumentException($"unknown argument '{arg}'.");
            }
        }

        return new Options(
            store,
            connection ?? Environment.GetEnvironmentVariable(ConnectionVariable),
            reuse,
            iterations,
            warmup,
            label,
            host,
            json);
    }

    private static (string Key, string? Value) Split(string arg)
    {
        var separator = arg.IndexOf('=', StringComparison.Ordinal);
        return separator < 0 ? (arg, null) : (arg[..separator], arg[(separator + 1)..]);
    }
}
