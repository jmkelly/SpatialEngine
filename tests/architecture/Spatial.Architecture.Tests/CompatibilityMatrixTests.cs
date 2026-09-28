using System.Text.RegularExpressions;

namespace Spatial.Architecture.Tests;

/// <summary>
/// The GeoServices compatibility matrices
/// (<c>research/compat/feature-service.md</c> and
/// <c>research/compat/map-service.md</c>) are written as snapshots, and a
/// snapshot goes stale silently: on SpatialEngine-itu both files still
/// marked <c>generateRenderer</c>, <c>legend</c>, the attachment surface,
/// <c>validateSQL</c>, the service-level query and the ADR-0056 modern query
/// params as **Missing** months after the routes landed. A branch that read
/// those rows believed pre-merge reality, and the merge had to decide which
/// side was true from the code instead of the document.
///
/// The rule: a row marked **Missing** must not name anything the adapter
/// actually serves. Both the mounted routes and the request parameters the
/// query parser reads are read out of the adapter source, so the guard
/// follows the code rather than a hand-kept list — a route that lands makes
/// the next stale row fail without anyone editing the test. Only the
/// capability cell is read, and only its backticked tokens, which is how the
/// matrices name an operation or a parameter. Non-goal, rejected and partial
/// rows are deliberately out of scope: a mounted operation that rejects by
/// name is honestly documented as a non-goal, and a partial row states the
/// part that is missing.
/// </summary>
public sealed class CompatibilityMatrixTests
{
    private static readonly Lazy<string> Root = new(RepositoryScanner.FindRepositoryRoot);

    /// <summary>Matches the <c>/{service}/FeatureServer/…</c> and <c>/{service}/MapServer/…</c> route literals.</summary>
    private static readonly Regex RouteLiteral = new(
        @"/\{service\}/(?<family>Feature|Map)Server/(?<tail>[A-Za-z0-9_{}*.:/-]+)",
        RegexOptions.Compiled);

    /// <summary>Matches a parameter the query parser reads by name.</summary>
    private static readonly Regex ParameterRead = new(
        @"\bGet(?:Bool|Int|Long|Double|Number|String|List|Values|Time)?\(\s*""(?<name>[A-Za-z][A-Za-z0-9]*)""",
        RegexOptions.Compiled);

    /// <summary>Matches a backticked identifier in a table cell.</summary>
    private static readonly Regex CellIdentifier = new(
        @"`(?<ticked>[A-Za-z./][A-Za-z0-9_ /.:{}*-]*)`",
        RegexOptions.Compiled);

    /// <summary>The first identifier inside one backticked cell token.</summary>
    private static readonly Regex LeadingIdentifier = new(@"[A-Za-z][A-Za-z0-9_]*", RegexOptions.Compiled);

    /// <summary>Words that name a shape or a concept, not an operation or a parameter.</summary>
    private static readonly HashSet<string> Prose = new(StringComparer.Ordinal)
    {
        "served", "Missing", "non-goal", "S4", "S3", "S1", "S2",
    };

    public static TheoryData<string, string> Matrices => new()
    {
        { "feature-service.md", "Feature" },
        { "map-service.md", "Map" },
    };

    [Theory]
    [MemberData(nameof(Matrices))]
    public void AMissingRowNamesNothingTheAdapterServes(string file, string family)
    {
        var matrix = Path.Combine(Root.Value, "research", "compat", file);
        Assert.True(File.Exists(matrix), $"Compatibility matrix not found: {matrix}");

        var served = ServedOperations(family);
        var parameters = ServedParameters();
        var offenders = new List<string>();

        foreach (var row in Rows(File.ReadAllLines(matrix)))
        {
            if (!row.Status.Equals("**Missing**", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var name in Identifiers(row.Capability))
            {
                if (Prose.Contains(name))
                {
                    continue;
                }

                if (served.Contains(name))
                {
                    offenders.Add($"{file}: a **Missing** row names the mounted {family}Server operation '{name}': {row.Capability}");
                }
                else if (parameters.Contains(name))
                {
                    offenders.Add($"{file}: a **Missing** row names the request parameter '{name}' the query parser reads: {row.Capability}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Stale compatibility rows — the engine serves these, so the row is wrong, not the code:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The operation names the adapter mounts for one server family.</summary>
    private static HashSet<string> ServedOperations(string family)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in AdapterSources())
        {
            foreach (Match match in RouteLiteral.Matches(File.ReadAllText(path)))
            {
                if (!match.Groups["family"].Value.Equals(family, StringComparison.Ordinal))
                {
                    continue;
                }

                var segments = match.Groups["tail"].Value
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Where(segment => !segment.StartsWith('{'))
                    .ToArray();
                if (segments.Length > 0)
                {
                    names.Add(segments[^1]);
                }
            }
        }

        return names;
    }

    /// <summary>The request parameters the adapter's query parsers read by name.</summary>
    private static HashSet<string> ServedParameters()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in AdapterSources())
        {
            foreach (Match match in ParameterRead.Matches(File.ReadAllText(path)))
            {
                names.Add(match.Groups["name"].Value);
            }
        }

        return names;
    }

    private static IEnumerable<string> AdapterSources() =>
        Directory.EnumerateFiles(Path.Combine(Root.Value, "src", "Spatial.Adapter.GeoServices"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>The §1 capability table rows of one matrix, as (capability, status) pairs.</summary>
    private static IEnumerable<(string Capability, string Status)> Rows(IEnumerable<string> lines)
    {
        var inTable = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inTable = line.StartsWith("## 1.", StringComparison.Ordinal);
                continue;
            }

            if (!inTable || !line.StartsWith('|'))
            {
                continue;
            }

            var cells = line.Trim('|').Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length < 4 || cells[0].Equals("---", StringComparison.Ordinal) || cells[0].Equals("ArcGIS capability", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (cells[0], cells[2]);
        }
    }

    /// <summary>
    /// The identifiers one capability cell names, read from its backticked
    /// tokens: the matrices name an operation or a parameter in backticks by
    /// convention, and a token may decorate it (``outFields``*,
    /// `.../<layerId>/query`, `/FeatureServer/query`), so the leading
    /// identifier of each token is the one that can name something served.
    /// </summary>
    private static IEnumerable<string> Identifiers(string capability)
    {
        foreach (Match match in CellIdentifier.Matches(capability))
        {
            var leading = LeadingIdentifier.Match(match.Groups["ticked"].Value);
            if (leading.Success)
            {
                yield return leading.Value;
            }
        }
    }
}
