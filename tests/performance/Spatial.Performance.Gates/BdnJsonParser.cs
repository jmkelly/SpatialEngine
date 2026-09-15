using System.Text.Json;

namespace Spatial.Performance.Gates;

/// <summary>
/// Parses BenchmarkDotNet <c>--exporters json</c> full reports into the
/// canonical <c>Type.Method</c> samples the baseline gate compares.
///
/// The nightly runs <c>--job Medium</c> over classes carrying
/// <c>[ShortRunJob]</c>, so both Medium and Short rows exist for the same
/// bench (T-086): the parser keeps the Medium row and drops the Short one.
/// Rows missing a mean, or the whole file missing Benchmarks, are skipped
/// with a warning instead of failing the parse — the gate fails later on
/// the missing bench, naming it.
/// </summary>
public static class BdnJsonParser
{
    /// <summary>Parsed samples plus the rows that were skipped, with reasons.</summary>
    public sealed record ParseResult(
        IReadOnlyDictionary<string, BenchSample> Samples,
        IReadOnlyList<string> Warnings);

    /// <summary>Parses one <c>*-report-full-compressed.json</c> payload.</summary>
    public static ParseResult ParseReport(string json)
    {
        var samples = new Dictionary<string, BenchSample>(StringComparer.Ordinal);
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        var warnings = new List<string>();

        using var document = JsonDocument.Parse(json);
        if (!TryGetBenchmarks(document, warnings, out var benchmarks))
        {
            return new ParseResult(samples, warnings);
        }

        foreach (var bench in benchmarks.EnumerateArray())
        {
            ParseRow(bench, samples, ranks, warnings);
        }

        return new ParseResult(samples, warnings);
    }

    private static bool TryGetBenchmarks(JsonDocument document, List<string> warnings, out JsonElement benchmarks)
    {
        benchmarks = default;
        if (document.RootElement.TryGetProperty("Benchmarks", out var found)
            && found.ValueKind == JsonValueKind.Array)
        {
            benchmarks = found;
            return true;
        }

        warnings.Add("report has no Benchmarks array; nothing parsed.");
        return false;
    }

    private static void ParseRow(
        JsonElement bench,
        Dictionary<string, BenchSample> samples,
        Dictionary<string, int> ranks,
        List<string> warnings)
    {
        if (!TryReadName(bench, warnings, out var name))
        {
            return;
        }

        if (!TryReadMean(bench, warnings, name, out var mean))
        {
            return;
        }

        if (samples.TryGetValue(name, out _) && ranks[name] <= ReadRank(bench))
        {
            return;
        }

        ranks[name] = ReadRank(bench);
        samples[name] = new BenchSample(name, mean, ReadAllocated(bench));
    }

    private static bool TryReadName(JsonElement bench, List<string> warnings, out string name)
    {
        name = string.Empty;
        if (bench.TryGetProperty("Type", out var typeElement)
            && bench.TryGetProperty("Method", out var methodElement)
            && typeElement.ValueKind == JsonValueKind.String
            && methodElement.ValueKind == JsonValueKind.String)
        {
            name = $"{typeElement.GetString()}.{methodElement.GetString()}";
            return true;
        }

        warnings.Add("skipped a row without Type/Method.");
        return false;
    }

    private static bool TryReadMean(JsonElement bench, List<string> warnings, string name, out double mean)
    {
        mean = 0;
        if (bench.TryGetProperty("Statistics", out var statistics)
            && statistics.TryGetProperty("Mean", out var meanElement)
            && meanElement.ValueKind == JsonValueKind.Number)
        {
            mean = meanElement.GetDouble();
            return true;
        }

        warnings.Add($"skipped {name}: no Statistics.Mean.");
        return false;
    }

    private static double ReadAllocated(JsonElement bench)
    {
        if (bench.TryGetProperty("Memory", out var memory)
            && memory.TryGetProperty("BytesAllocatedPerOperation", out var allocatedElement)
            && allocatedElement.ValueKind == JsonValueKind.Number)
        {
            return allocatedElement.GetDouble();
        }

        return 0;
    }

    private static int ReadRank(JsonElement bench)
    {
        if (bench.TryGetProperty("DisplayInfo", out var displayElement)
            && displayElement.ValueKind == JsonValueKind.String
            && (displayElement.GetString() ?? string.Empty).Contains("Medium", StringComparison.Ordinal))
        {
            return 0;
        }

        return 1;
    }

    /// <summary>Parses every <c>*.json</c> report under a BDN artifacts directory.</summary>
    public static ParseResult ParseDirectory(string directory)
    {
        var merged = new Dictionary<string, BenchSample>(StringComparer.Ordinal);
        var warnings = new List<string>();

        if (!Directory.Exists(directory))
        {
            warnings.Add($"results directory {directory} does not exist; nothing parsed.");
            return new ParseResult(merged, warnings);
        }

        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).ToList();
        if (files.Count == 0)
        {
            warnings.Add($"no JSON reports under {directory}; nothing parsed.");
            return new ParseResult(merged, warnings);
        }

        foreach (var file in files.Order(StringComparer.Ordinal))
        {
            ParseResult parsed;
            try
            {
                parsed = ParseReport(File.ReadAllText(file));
            }
            catch (JsonException exception)
            {
                warnings.Add($"skipped {file}: not valid JSON ({exception.Message}).");
                continue;
            }

            foreach (var warning in parsed.Warnings)
            {
                warnings.Add($"{file}: {warning}");
            }

            foreach (var sample in parsed.Samples.Values)
            {
                merged[sample.Name] = sample;
            }
        }

        return new ParseResult(merged, warnings);
    }
}
