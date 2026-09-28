using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Spatial.Architecture.Tests;

/// <summary>
/// ADR numbering is a reference scheme, so it is only useful if a number
/// identifies exactly one record. Parallel branches once landed four records
/// as ADR-0074 and two as ADR-0070, which made a prose "ADR-0074" ambiguous:
/// the GeoServices compatibility note meant ground-distance buffering while the
/// distilled contracts meant the feature-query plan. Nothing caught it because
/// the references are prose, not links.
///
/// The rule: the first record to claim a number keeps it, and a later
/// colliding record takes the next free number (SpatialEngine-u2x.24). This
/// suite enforces the invariant that made the collision harmful, and — since
/// enforcing it after the merge is what left the renumbering to the
/// coordinator — it also enforces that the allocator, which reserves a number
/// before the record is written, agrees with the tree (SpatialEngine-u2x.29).
/// </summary>
public sealed class AdrNumberingTests
{
    private static readonly Lazy<string> Root = new(RepositoryScanner.FindRepositoryRoot);

    /// <summary>Matches a bare ADR number, in both prose and slug forms.</summary>
    private static readonly Regex AdrReference = new(@"ADR-(?<number>\d{4})(?!\d)", RegexOptions.Compiled);

    /// <summary>Matches a register row's leading bare <c>NNNN</c> number.</summary>
    private static readonly Regex RegisterRow = new(@"^\|\s*(?<number>\d{4})\s*\|", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Matches the leading <c>ADR-NNNN</c> of a decision-record file name.</summary>
    private static readonly Regex AdrFileName = new(@"^ADR-(?<number>\d{4})-", RegexOptions.Compiled);

    private static readonly Regex AdrTitle = new(@"^#\s+ADR-(?<number>\d{4})(?!\d)", RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly string[] TextPatterns =
    [
        "*.cs", "*.csproj", "*.slnx", "*.md", "*.props", "*.targets",
        "*.json", "*.sh", "*.py", "*.ts", "*.mjs",
    ];

    private static readonly HashSet<string> GeneratedSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", "node_modules", "StrykerOutput",
        "artifacts", ".aspire", "data", "test-results", "playwright-report",
    };

    /// <summary>
    /// No two decision records share a number. A collision silently makes every
    /// bare reference to that number ambiguous, so it is a defect, not a
    /// cosmetic one.
    /// </summary>
    [Fact]
    public void Adr_numbers_are_unique()
    {
        var collisions = DecisionRecords()
            .GroupBy(record => record.Number, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group =>
                $"{group.Key} is claimed by {group.Count()} records: " +
                $"{string.Join(", ", group.Select(r => r.FileName))}. " +
                "Renumber all but the first; the earliest claimant keeps the number.")
            .ToList();

        Assert.Empty(collisions);
    }

    /// <summary>
    /// A record's own title heading carries its number, so the heading and the
    /// file name can never drift apart across a renumber.
    /// </summary>
    [Fact]
    public void Adr_title_heading_matches_its_file_name()
    {
        var violations = DecisionRecords()
            .Where(record => record.TitleNumber is null)
            .Select(record =>
                $"{record.FileName} has no '# ADR-NNNN: ...' title heading, or the heading does not start with {record.Number}.")
            .Concat(DecisionRecords()
                .Where(record => record.TitleNumber is not null && record.TitleNumber != record.Number)
                .Select(record =>
                    $"{record.FileName} is titled ADR-{record.TitleNumber} but filed under {record.Number}."))
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// Every <c>ADR-NNNN</c> mentioned anywhere in the tree resolves to exactly
    /// one existing record, so a reader can always follow a bare reference to
    /// the single file it means. A dangling number is a broken citation; an
    /// ambiguous one is the collision this suite exists to prevent.
    /// </summary>
    [Fact]
    public void Every_adr_reference_resolves_to_exactly_one_record()
    {
        var known = DecisionRecords()
            .GroupBy(record => record.Number, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var citations = TextFiles()
            .SelectMany(path => AdrCitations(File.ReadAllText(path))
                .Select(number => (Path: path, Number: number)))
            .ToList();

        var violations = citations
            .Where(reference =>
                !known.TryGetValue(reference.Number, out var count) || count != 1)
            .Select(reference =>
            {
                var relative = Path.GetRelativePath(Root.Value, reference.Path);
                return known.TryGetValue(reference.Number, out var count)
                    ? $"{relative} cites ADR-{reference.Number}, which is claimed by {count} records."
                    : $"{relative} cites ADR-{reference.Number}, which has no record in architecture/decisions.";
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(violations);
    }

    /// <summary>
    /// The distilled ADR register lists one row per decision, so a number that
    /// appears in two rows makes the register contradict itself and the number
    /// unciteable — even when the decision records behind it are numbered
    /// correctly. The citation test above cannot see this: the register cites
    /// bare <c>0074</c>, not <c>ADR-0074</c>. SpatialEngine-u2x.31.
    /// </summary>
    [Fact]
    public void Distilled_adr_register_lists_each_number_once()
    {
        var duplicates = RegisterRows()
            .GroupBy(number => number, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"the ADR register lists {group.Key} {group.Count()} times")
            .ToList();

        Assert.Empty(duplicates);
    }

    /// <summary>Every bare ADR number in the register table, with repeats.</summary>
    private static IEnumerable<string> RegisterRows()
    {
        var readme = File.ReadAllText(Path.Combine(Root.Value, "architecture", "distilled", "README.md"));

        // The register is the "## ADR register" section only: the "Route by task"
        // table above it repeats numbers by design (one row per task).
        var heading = readme.IndexOf("## ADR register", StringComparison.Ordinal);
        if (heading < 0)
        {
            return [];
        }

        var end = readme.IndexOf("\n## ", heading + 1, StringComparison.Ordinal);
        var register = end < 0 ? readme[heading..] : readme[heading..end];

        return RegisterRow.Matches(register).Select(match => match.Groups["number"].Value);
    }

    /// <summary>The number the allocator would hand to a new branch today.</summary>
    [Fact]
    public void The_allocator_hands_out_a_number_no_record_claims()
    {
        // The reservation (tools/adr-next-number.py --reserve) is the step
        // that stops parallel branches picking the same number
        // (SpatialEngine-u2x.29). It is only as good as the claim set it reads,
        // so this suite holds the allocator to the tree: the number it hands
        // out has to be free in architecture/decisions, or the first branch to
        // use it is a merge-time collision.
        var claimed = DecisionRecords().Select(record => record.Number).ToList();
        var handed = Allocator().Output;

        Assert.Matches(@"^\d{4}$", handed);
        Assert.DoesNotContain(handed, claimed);
        Assert.Equal(
            (int.Parse(Highest(claimed), CultureInfo.InvariantCulture) + 1)
                .ToString("D4", CultureInfo.InvariantCulture),
            handed);
    }

    /// <summary>
    /// <c>--check</c> is the gate a worker runs immediately before writing its
    /// record, so it has to refuse every number that is already spoken for and
    /// clear the one that is not. A <c>--check</c> that wrongly passed is the
    /// collision arriving late; one that wrongly failed is a branch unable to
    /// finish. Every number in the tree is asked about in one run: the gate is
    /// not worth a minute of process start-up.
    /// </summary>
    [Fact]
    public void The_allocator_check_gate_agrees_with_the_records()
    {
        var claimed = DecisionRecords().Select(record => record.Number).ToList();
        var next = (int.Parse(Highest(claimed), CultureInfo.InvariantCulture) + 1)
            .ToString("D4", CultureInfo.InvariantCulture);

        // The tool prints the number and nothing else when it is free, and a
        // sentence naming the holder when it is not.
        var verdicts = Allocator(["--check", .. claimed, next]);
        var cleared = verdicts.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !line.Contains("ADR-", StringComparison.Ordinal))
            .ToList();

        var violations = claimed
            .Where(number => cleared.Contains(number))
            .Select(number => $"ADR-{number} has a record, but --check reports "
                              + "it as free, so a branch would write a second "
                              + "record claiming it.")
            .ToList();

        if (!cleared.Contains(next))
        {
            violations.Add(
                $"--check refuses {next} ({verdicts.Output}) but no record " +
                "claims that number, so a branch holding it would be blocked.");
        }

        Assert.Empty(violations);
    }

    /// <summary>The highest number in the tree, as the four digits it is filed under.</summary>
    private static string Highest(IEnumerable<string> numbers) =>
        numbers.OrderBy(number => number, StringComparer.Ordinal).Last();

    /// <summary>Runs the allocator from the repository root.</summary>
    private static (int ExitCode, string Output, string Error) Allocator(
        params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "python3",
            WorkingDirectory = Root.Value,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("tools/adr-next-number.py");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException(
            "python3 could not be started; the ADR allocator is run with it, " +
            "and without it a branch has no way to reserve a number.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output.Trim(), error.Trim());
    }

    /// <summary>Every bare ADR number cited by the text, with repeats.</summary>
    private static IEnumerable<string> AdrCitations(string text) =>
        AdrReference.Matches(text).Select(match => match.Groups["number"].Value);

    private static IEnumerable<AdrRecord> DecisionRecords() =>
        Directory.EnumerateFiles(
                Path.Combine(Root.Value, "architecture", "decisions"),
                "ADR-*.md",
                SearchOption.TopDirectoryOnly)
            .Select(path => new AdrRecord(
                Path.GetFileName(path),
                AdrFileName.Match(Path.GetFileName(path)).Groups["number"].Value,
                AdrTitle.Match(File.ReadAllText(path)).Groups["number"].Success
                    ? AdrTitle.Match(File.ReadAllText(path)).Groups["number"].Value
                    : null));

    private static IEnumerable<string> TextFiles() => TextPatterns
        .SelectMany(pattern => Directory.EnumerateFiles(Root.Value, pattern, SearchOption.AllDirectories))
        .Where(path => !IsExcluded(path));

    /// <summary>
    /// Build and test output, vendored dependencies and this guard itself: the
    /// suite's own "ADR-NNNN" patterns would otherwise cite themselves.
    /// </summary>
    private static bool IsExcluded(string path)
    {
        if (string.Equals(Path.GetFileName(path), "AdrNumberingTests.cs", StringComparison.Ordinal))
        {
            return true;
        }

        var relative = Path.GetRelativePath(Root.Value, path);
        return relative.Split(Path.DirectorySeparatorChar).Any(GeneratedSegments.Contains);
    }

    private sealed record AdrRecord(string FileName, string Number, string? TitleNumber);
}
