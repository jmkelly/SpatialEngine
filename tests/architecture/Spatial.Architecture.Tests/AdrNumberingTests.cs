using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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

    /// <summary>
    /// The ref the allocator allocates against, resolved the way it resolves
    /// one. A claim is a record on this ref <em>or</em> in the tree, so the
    /// gate has to read it too: a base that has moved on is not a flake, it is
    /// a number this branch must not hand out.
    /// </summary>
    private static readonly Lazy<string> BaseRef = new(ResolveBaseRef);

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
        var claimed = ClaimedNumbers();
        using var store = new ScratchStore();
        var handed = Gate(store, []).Output;

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
        var claimed = ClaimedNumbers();
        var next = OnePast(claimed);

        // The tool prints the number and nothing else when it is free, and a
        // sentence naming the holder when it is not. The store is the one input
        // the gate does not own: the repository's own is swarm-wide
        // coordination state (ADR-0090) that changes while the gate runs, so
        // the gate reads a store it created (SpatialEngine-u2x.33).
        using var store = new ScratchStore();
        var verdicts = Gate(store, ["--check", .. claimed, next]);
        var cleared = Cleared(verdicts.Output);

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

    /// <summary>
    /// The reproduction for SpatialEngine-u2x.33: a live reservation for the
    /// number the gate probes is exactly what turned the gate red. The
    /// reservation store is shared by every worktree on purpose (ADR-0090), so
    /// while parallel branches held 0089-0098, <c>--check</c> correctly refused
    /// the number this tree would otherwise hand out, and the gate above
    /// reported a violation for a record that does not exist — on every branch
    /// in the repo, from a file none of them had touched. The reservation here
    /// is written into a store the test creates, so the reproduction never
    /// touches the real one.
    ///
    /// It also read as red on every ci run, where it failed for the opposite
    /// reason: <c>--check</c> cleared the number, because the holder this test
    /// fabricated came from whatever refs the checkout had (on the runner that
    /// was <c>origin</c>, the remote alias, under a checkout with no local
    /// branches of its own), and the allocator looked the holder up under
    /// <c>refs/heads</c> only, judged the hold dead and swept it — a live
    /// reservation unlinked, which is the collision the sweep exists to
    /// prevent. Both halves are fixed (SpatialEngine-ivp): the allocator reads
    /// the holder wherever its ref names it, and <see cref="LiveBranch"/> makes
    /// this test's holder a branch that exists rather than whatever the
    /// checkout happened to be carrying.
    /// </summary>
    [Fact]
    public void The_check_gate_refuses_a_number_a_live_reservation_holds()
    {
        var next = OnePast(ClaimedNumbers());
        using var holder = new LiveBranch();

        using var store = new ScratchStore();
        store.Hold(next, holder.Name);

        var verdicts = Gate(store, ["--check", next]);

        Assert.Equal(1, verdicts.ExitCode);
        Assert.Contains(holder.Name, verdicts.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(next, Cleared(verdicts.Output));
    }

    /// <summary>
    /// ...which is only a defect if the gate is reading the store the swarm is
    /// writing to. This holds the same live reservation — the one that reddened
    /// every branch — in a <em>different</em> store and asserts the gate still
    /// clears the number, and that the store the gate reads is not the one the
    /// reservation landed in. A reservation taken after this test finished
    /// cannot change the verdict either, because the store it lands in is not
    /// one the gate looks at.
    /// </summary>
    [Fact]
    public void The_check_gate_cannot_be_flipped_by_reservations_in_another_store()
    {
        var claimed = ClaimedNumbers();
        var next = OnePast(claimed);

        // The swarm's store, and the store the gate reads: two different ones.
        using var shared = new ScratchStore();
        using var owned = new ScratchStore();
        using var holder = new LiveBranch();
        shared.Hold(next, holder.Name);

        Assert.NotEqual(
            Path.GetFullPath(shared.Store), Path.GetFullPath(owned.Store));

        var verdicts = Gate(owned, ["--check", .. claimed, next]);
        var cleared = Cleared(verdicts.Output);

        // Only the number no record claims comes back free, which is the whole
        // verdict: it is the number this test was reddened over.
        Assert.Equal([next], cleared);
    }

    /// <summary>
    /// The numbers the allocator refuses: a record on the base ref or in the
    /// tree. Reading the tree alone is not its rule, and a branch that has not
    /// yet taken main's newest record still has to see that number as claimed —
    /// otherwise the gate and the allocator disagree about a number neither of
    /// them is wrong about.
    /// </summary>
    private static List<string> ClaimedNumbers() =>
        DecisionRecords().Select(record => record.Number)
            .Concat(BaseRecords())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>The decision records on the base ref, listed the way the allocator lists them.</summary>
    private static IEnumerable<string> BaseRecords() =>
        Git("ls-tree", "-r", "--name-only", BaseRef.Value, "--", "architecture", "decisions")
            .Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => AdrFileName.Match(Path.GetFileName(line.Trim())).Groups["number"].Value)
            .Where(number => number.Length > 0);

    /// <summary>The ref the allocator allocates against, resolved the way it resolves one.</summary>
    private static string ResolveBaseRef()
    {
        foreach (var candidate in (string[])["origin/main", "main"])
        {
            if (Git("rev-parse", "--verify", "--quiet", $"{candidate}^{{commit}}").ExitCode == 0)
            {
                return candidate;
            }
        }

        return "HEAD";
    }

    /// <summary>One past the highest claim, as the four digits it is filed under.</summary>
    private static string OnePast(IEnumerable<string> claimed) =>
        (int.Parse(Highest(claimed), CultureInfo.InvariantCulture) + 1)
            .ToString("D4", CultureInfo.InvariantCulture);

    /// <summary>
    /// A branch that exists, and is not the one under test, so the allocator
    /// reads the reservation as another worktree's hold rather than as this
    /// branch re-reserving a number it already holds.
    ///
    /// It has to exist. The earlier version named whatever ref
    /// <c>for-each-ref</c> offered first, and a shallow
    /// <c>actions/checkout</c> carries none but remote ones — so on the runner
    /// the holder was <c>origin</c>, a ref no branch is checked out from, the
    /// allocator read the hold as a branch that no longer existed and swept it,
    /// and the test asserted its opposite and failed (SpatialEngine-ivp). A
    /// checkout with no local branch of its own gets one here, so "live" is a
    /// property of the reproduction rather than of the machine running it.
    /// </summary>
    private sealed class LiveBranch : IDisposable
    {
        private readonly bool _created;

        public string Name { get; }

        public LiveBranch()
        {
            var here = Git("rev-parse", "--abbrev-ref", "HEAD").Output;
            var existing = Git("for-each-ref", "--format=%(refname:short)", "refs/heads")
                .Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(name => name != here)
                .ToList();

            if (existing.Count > 0)
            {
                Name = existing[0];
                return;
            }

            Name = $"adr-hold-{Guid.NewGuid():N}";
            var created = Git("branch", Name);
            if (created.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"a local branch for the reservation could not be created " +
                    $"({created.Error}), so the gate would run against a holder " +
                    "that does not exist and the reproduction would not be one.");
            }

            _created = true;
        }

        public void Dispose()
        {
            if (_created)
            {
                Git("branch", "-D", Name);
            }
        }
    }

    /// <summary>
    /// The numbers <c>--check</c> cleared: the bare four digits it prints for a
    /// free number, with the sentences it prints for a taken one left out.
    /// </summary>
    private static List<string> Cleared(string output) =>
        output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !line.Contains("ADR-", StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Runs the allocator the way a worker does — on the real repository, so
    /// the claim set is the real one — but pinned to the base ref this gate
    /// resolved and to a reservation store the test owns.
    /// </summary>
    private static (int ExitCode, string Output, string Error) Gate(
        ScratchStore store, params string[] arguments) =>
        Allocator(["--base", BaseRef.Value, "--reservation-dir", store.Store, .. arguments]);

    /// <summary>
    /// A private reservation store: a directory the test creates, hands to the
    /// allocator and deletes. No agent reserves against it, and it reads none
    /// of the shared store.
    /// </summary>
    private sealed class ScratchStore : IDisposable
    {
        public string Store { get; }

        public ScratchStore()
        {
            Store = Path.Combine(Path.GetTempPath(), $"adr-reservations-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Store);
        }

        /// <summary>
        /// A reservation a live branch holds: it exists, it is minutes old, and
        /// nothing about it reads as stale — the state the shared store was in
        /// when it reddened the gate on every branch.
        /// </summary>
        public void Hold(string number, string branch) =>
            File.WriteAllText(
                Path.Combine(Store, $"{number}.json"),
                JsonSerializer.Serialize(new
                {
                    number,
                    branch,
                    bead = "SpatialEngine-u2x.33",
                    baseRef = BaseRef.Value,
                    reserved_at = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}+00:00",
                }));

        public void Dispose()
        {
            if (Directory.Exists(Store))
            {
                Directory.Delete(Store, recursive: true);
            }
        }
    }

    /// <summary>The highest number in the tree, as the four digits it is filed under.</summary>
    private static string Highest(IEnumerable<string> numbers) =>
        numbers.OrderBy(number => number, StringComparer.Ordinal).Last();

    /// <summary>Runs the allocator from the repository root.</summary>
    private static (int ExitCode, string Output, string Error) Allocator(
        params string[] arguments) =>
        Run("python3", "tools/adr-next-number.py", arguments);

    /// <summary>Runs git from the repository root, for the base the allocator reads.</summary>
    private static (int ExitCode, string Output, string Error) Git(
        params string[] arguments) => Run("git", null, arguments);

    private static (int ExitCode, string Output, string Error) Run(
        string fileName, string? firstArgument, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = Root.Value,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (firstArgument is not null)
        {
            start.ArgumentList.Add(firstArgument);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException(
            $"{fileName} could not be started; the ADR allocator is run with "
            + "python3 and reads the tree with git, and without them a branch "
            + "has no way to reserve a number.");

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
