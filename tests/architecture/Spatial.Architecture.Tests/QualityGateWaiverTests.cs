using System.Text.Json;

namespace Spatial.Architecture.Tests;

/// <summary>
/// CRAP-gate waiver hygiene (T-009): every entry in
/// <c>quality-waivers.json</c> names a real method in a real source file and
/// carries the reason and evidence for the waiver, so a coverage-matching
/// artifact is investigated once and never re-queued without fresh data.
/// </summary>
public sealed class QualityGateWaiverTests
{
    [Fact]
    public void Crap_waivers_are_documented_and_reference_real_methods()
    {
        var root = RepositoryScanner.FindRepositoryRoot();
        var path = Path.Combine(root, "quality-waivers.json");
        Assert.True(File.Exists(path), "quality-waivers.json must exist at the repo root to record CRAP-gate exceptions (T-009).");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var hasWaivers = document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("waivers", out var waivers)
            && waivers.ValueKind == JsonValueKind.Array;
        Assert.True(hasWaivers,
            "quality-waivers.json must be an object with a 'waivers' array.");
        var entries = document.RootElement.GetProperty("waivers");

        var violations = new List<string>();
        foreach (var entry in entries.EnumerateArray())
        {
            var method = Required(entry, "method", violations);
            var file = Required(entry, "file", violations);
            Required(entry, "reason", violations);
            Required(entry, "evidence", violations);
            if (method is null || file is null)
            {
                continue;
            }

            var source = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            // The waiver names Class.Method for the audit queue; the source
            // spells only the simple name, so match on the part after the dot.
            var simpleName = method.Contains('.') ? method[(method.LastIndexOf('.') + 1)..] : method;
            if (!File.Exists(source))
            {
                violations.Add($"waiver for {method} points at missing file {file}.");
            }
            else if (!File.ReadAllText(source).Contains(simpleName, StringComparison.Ordinal))
            {
                violations.Add($"waiver for {method} no longer matches {file}: the method is gone or renamed, so re-investigate instead of carrying a stale waiver.");
            }
        }

        Assert.Empty(violations);
    }

    private static string? Required(JsonElement entry, string property, List<string> violations)
    {
        if (entry.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString();
        }

        violations.Add($"a quality waiver is missing a non-empty '{property}': {entry}.");
        return null;
    }
}
