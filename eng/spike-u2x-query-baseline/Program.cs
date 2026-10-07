using System.Globalization;
using System.Text.Json;
using Spatial.Contracts;
using Spatial.Core.Features.Query;
using Spatial.Contracts.Providers;

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
        Console.WriteLine($"# A=ScanAsync+in-adapter  B=store pushdown (a reduction uses the store's reduction face)  Bp=B with order+limit  C=IFeatureLookup by identity  D=emulated full pushdown (ceiling, not shipped)");
        Console.WriteLine();
        Console.WriteLine(SampleReport.Header);

        var reports = new List<SampleReport>();
        var identityUnavailable = runner.IdentityUnavailable;
        if (identityUnavailable is { } reason)
        {
            Console.WriteLine($"# path C (IFeatureLookup by identity) is unavailable here: {reason}");
        }

        if (FeatureReadNote(loaded.Description, options.Store) is { } note)
        {
            Console.WriteLine($"# {note}");
        }

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
                reports.Add(await MeasureAsync(options, attributePushdown ? "B-push" : "B-bbox", request, variant, where, Pushdown(runner, request, variant, where, storeFilter, paged: false), cancellationToken));
                reports.Add(await MeasureAsync(options, "Bp-page", request, variant, where, Pushdown(runner, request, variant, where, storeFilter, paged: true), cancellationToken));
                reports.Add(await MeasureAsync(options, "D-emul", request, variant, where, Emulated(runner, request, variant, where), cancellationToken));
                if (identityUnavailable is null && !QueryRequest.IsCountOnly(variant) && !QueryRequest.IsStatistics(variant))
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

    /// <summary>
    /// Why the <em>feature read</em> (the <c>page25</c> variant) is not a
    /// pushdown on the layer under test, or <c>null</c> when it is. Only the SQL
    /// stores can decline a pushdown this way: an in-process store evaluates the
    /// plan over the rows it already holds and names them by the ordinal of
    /// <em>that</em> set, so nothing it does renumbers anything.
    ///
    /// <para>
    /// A store may only push a restriction, or address a page, where the push
    /// is identity-preserving: a dataset that declares no identity column names
    /// its features by the ordinal of the read, so a <c>WHERE</c> reaching SQL
    /// would renumber them, and an <c>ORDER BY</c> with no identity tie-break is
    /// an order an <c>OFFSET</c> cannot name (ADR-0097 §1, ADR-0116 §1). Both
    /// stores therefore keep such a <em>read</em> whole and finish it with the
    /// reference executor over the whole read — the answer is right, the read is
    /// the table.
    /// </para>
    ///
    /// <para>
    /// A reduction is not a read and is not in this state (ADR-0184 §1): a
    /// count and a grouped reduction return values and no feature, so there is
    /// no ordinal for a restriction to renumber, and B and Bp on the
    /// <c>countOnly</c> and <c>statistics</c> variants are the store's own
    /// reduction face — nothing is materialised. The report says which path each
    /// number is for because the columns do not: <c>rows</c> counts what a call
    /// returned and <c>alloc_MB</c> follows what it built, so a whole read and a
    /// pushed reduction are only distinguishable when the note names them.
    /// </para>
    /// </summary>
    private static string? FeatureReadNote(DatasetDescription description, string storeKind) =>
        description.IdColumns.Count > 0 || !SqlStores.Contains(storeKind, StringComparer.Ordinal)
            ? null
            : $"the feature read (page25) is not a pushdown on this layer: dataset '{description.Id}' declares no "
                + "identity column, so a page on it is answered by the whole-layer read plus the reference executor, "
                + "and rows and alloc_MB follow the whole read. The reductions are: a count and a grouped reduction "
                + "return values and no feature, so B and Bp on the countOnly and statistics variants are the store's "
                + "own reduction face with rows=0 (ADR-0184 §1). Measure a feature-read pushdown on a layer that "
                + "declares an identity column.";

    /// <summary>The stores that answer a plan against a database rather than in process.</summary>
    private static readonly string[] SqlStores = ["postgis"];

    private static Func<CancellationToken, Task<Outcome>> Pushdown(
        QueryRunner runner,
        QueryRequest request,
        string variant,
        Predicate? where,
        Predicate? storeFilter,
        bool paged) =>
        (token) => runner.PushdownAsync(request, variant, where, storeFilter, paged, token);

    private static Func<CancellationToken, Task<Outcome>> Emulated(
        QueryRunner runner,
        QueryRequest request,
        string variant,
        Predicate? where) =>
        (token) => runner.EmulatedPushdownAsync(request, variant, where, token);

    private static async Task<SampleReport> MeasureAsync(
        Options options,
        string path,
        QueryRequest request,
        string variant,
        Predicate? where,
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
        var http = await HttpBaseline.RunAsync(
            host, options.HostService, options.HostLayer, options.Warmup, options.Iterations, cancellationToken);
        Console.WriteLine();
        Console.WriteLine($"# end-to-end over HTTP at {host} (FeatureServer service '{options.HostService}', {options.Iterations} iterations)");
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

        var paging = await HttpBaseline.PageThroughAsync(
            host, options.HostService, options.HostLayer, options.Warmup, cancellationToken);
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
    string HostService,
    string HostLayer,
    string? Json)
{
    private const string ConnectionVariable = "SPATIAL_POSTGIS_CONNECTION";

    /// <summary>The service the <c>--host</c> mode reads: the demo catalogue by default.</summary>
    internal const string DefaultHostService = "demo";

    /// <summary>The layer name the <c>--host</c> mode resolves under that service.</summary>
    internal const string DefaultHostLayer = "world_cities";

    internal static Options Parse(IReadOnlyList<string> args)
    {
        string store = "memory";
        string? connection = null;
        string? host = null;
        string? json = null;
        var hostService = DefaultHostService;
        var hostLayer = DefaultHostLayer;
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
                case "--host-service": hostService = value ?? DefaultHostService; break;
                case "--host-layer": hostLayer = value ?? DefaultHostLayer; break;
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
            hostService,
            hostLayer,
            json);
    }

    private static (string Key, string? Value) Split(string arg)
    {
        var separator = arg.IndexOf('=', StringComparison.Ordinal);
        return separator < 0 ? (arg, null) : (arg[..separator], arg[(separator + 1)..]);
    }
}
