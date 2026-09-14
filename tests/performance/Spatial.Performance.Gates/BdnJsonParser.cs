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
        if (!document.RootElement.TryGetProperty("Benchmarks", out var benchmarks)
            || benchmarks.ValueKind != JsonValueKind.Array)
        {
            warnings.Add("report has no Benchmarks array; nothing parsed.");
            return new ParseResult(samples, warnings);
        }

        foreach (var bench in benchmarks.EnumerateArray())
        {
            if (!bench.TryGetProperty("Type", out var typeElement)
                || !bench.TryGetProperty("Method", out var methodElement)
                || typeElement.ValueKind != JsonValueKind.String
                || methodElement.ValueKind != JsonValueKind.String)
            {
                warnings.Add("skipped a row without Type/Method.");
                continue;
            }

            var name = $"{typeElement.GetString()}.{methodElement.GetString()}";
            if (!bench.TryGetProperty("Statistics", out var statistics)
                || !statistics.TryGetProperty("Mean", out var meanElement)
                || meanElement.ValueKind != JsonValueKind.Number)
            {
                warnings.Add($"skipped {name}: no Statistics.Mean.");
                continue;
            }

            var allocated = 0.0;
            if (bench.TryGetProperty("Memory", out var memory)
                && memory.TryGetProperty("BytesAllocatedPerOperation", out var allocatedElement)
                && allocatedElement.ValueKind == JsonValueKind.Number)
            {
                allocated = allocatedElement.GetDouble();
            }

            var rank = 1;
            if (bench.TryGetProperty("DisplayInfo", out var displayElement)
                && displayElement.ValueKind == JsonValueKind.String
                && (displayElement.GetString() ?? string.Empty).Contains("Medium", StringComparison.Ordinal))
            {
                rank = 0;
            }

            if (samples.TryGetValue(name, out _) && ranks[name] <= rank)
            {
                continue;
            }

            ranks[name] = rank;
            samples[name] = new BenchSample(name, meanElement.GetDouble(), allocated);
        }

        return new ParseResult(samples, warnings);
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
