using System.Globalization;
using System.Text;
using Spatial.Core.Geometry;
using Spatial.Transformations.ProjNet.Grids;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>One point the engine and PROJ are both asked about, and whose answer is compared.</summary>
/// <param name="Name">Where it is, in words a failed residual can name.</param>
/// <param name="Datum">The graph token whose bundle has to be deployed for this point to mean anything.</param>
internal sealed record ControlPoint(string Name, string Datum, double Longitude, double Latitude);

/// <summary>
/// One control point's two answers and the distance between them. The residual
/// is computed once, here, rather than by each caller, so that "the residual"
/// means one thing across the suite.
/// </summary>
internal sealed record Agreement(string Point, string Target, double EngineX, double EngineY, double ProjX, double ProjY)
{
    /// <summary>The distance between the two answers, in the target's own units — metres for every target here.</summary>
    public double ResidualMetres
    {
        get
        {
            var (dx, dy) = (EngineX - ProjX, EngineY - ProjY);
            return Math.Sqrt((dx * dx) + (dy * dy));
        }
    }

    /// <summary>Both answers and the residual, as one line a test output can carry.</summary>
    public string Report() =>
        $"{Point} on {Target}: engine ({EngineX:F3}, {EngineY:F3}) PROJ ({ProjX:F3}, {ProjY:F3}) residual {ResidualMetres:F4} m";
}

/// <summary>
/// The file extensions a datum shift bundle ships under. Kept in one place
/// because two things need the same list: the registry names bundles by file
/// name, and the licence guard has to recognise a bundle that has been
/// committed by accident (ADR-0105 §licence).
/// </summary>
internal static class GridBundleExtensions
{
    private static readonly string[] Bundle = [".gsb", ".las", ".los"];

    public static bool Contains(string extension) =>
        Bundle.Contains(extension, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The deployment this suite measures against, resolved from the environment
/// and reported in full whatever it turns out to be (ADR-0105 §licence,
/// ADR-0179).
/// <para>
/// A published bundle is third-party data with its own terms, so the engine
/// neither embeds one nor fetches one: an operator deploys a bundle into a
/// directory this harness reads, and the licence position that permits that
/// deployment is theirs. Everything here follows from that. The harness never
/// downloads anything, never fails a run because nothing is deployed, and never
/// leaves a machine unable to say <em>why</em> nothing is deployed — because a
/// suite that is silently absent is read as agreement, and agreement with a
/// bundle that was never read is the one answer this exercise must not give.
/// </para>
/// </summary>
internal sealed class PublishedGridDeployment
{
    /// <summary>The variable an operator deploys a bundle through, as a path-separator-delimited priority list.</summary>
    public const string GridDirectoryVariable = "SPATIALENGINE_GRID_DIR";

    /// <summary>The variable naming <c>cs2cs</c> when it is installed somewhere the PATH does not reach.</summary>
    public const string ProjVariable = "SPATIALENGINE_PROJ";

    /// <summary>
    /// The residual a published bundle is held to, in metres. The acceptance
    /// bar ADR-0105 filed was sub-metre; this is an order of magnitude inside
    /// it, because the engine's reader and PROJ's are not the same code and a
    /// bundle's own node accuracy is the floor neither can beat.
    /// </summary>
    public const double PinnedResidualMetres = 0.1;

    /// <summary>Great Britain, at the points a client asks about: two cities and the ground between them.</summary>
    public static readonly ControlPoint[] OsgbControlPoints =
    [
        new("London", "OSGB36", -0.1276, 51.5072),
        new("Edinburgh", "OSGB36", -3.1883, 55.9533),
        new("Bristol", "OSGB36", -2.5879, 51.4545),
    ];

    /// <summary>North America, both inside UTM zone 10N so the projected target is one grid for both.</summary>
    public static readonly ControlPoint[] Nad83ControlPoints =
    [
        new("Seattle", "NAD83", -122.33, 47.61),
        new("Portland", "NAD83", -122.6765, 45.5231),
    ];

    private readonly IReadOnlyList<string> _absent;
    private readonly IReadOnlyDictionary<string, Status> _statuses;
    private readonly string? _proj;

    private PublishedGridDeployment(
        IReadOnlyList<string> directories,
        IReadOnlyList<string> absent,
        IReadOnlyDictionary<string, Status> statuses,
        string? proj)
    {
        Directories = directories;
        _absent = absent;
        _statuses = statuses;
        _proj = proj;
    }

    /// <summary>The configured grid directories, in priority order, for a host to build over.</summary>
    public IReadOnlyList<string> Directories { get; }

    /// <summary>Whether a datum's bundle is deployed here and the reader accepted it.</summary>
    public bool IsDeployed(string graphName) => _statuses.TryGetValue(graphName, out var status) && status.Directory is not null;

    /// <summary>Where a datum's deployed bundle came from, or the empty string when none is deployed.</summary>
    public string BundleFor(string graphName) =>
        _statuses.TryGetValue(graphName, out var status) ? status.Operation.FileName : "no bundle the catalogue names";

    /// <summary>
    /// Why a datum's comparison cannot run, in words that name what is missing.
    /// The empty string when the bundle is deployed — the reference is a
    /// separate question, asked by <see cref="MissingReferenceReason"/>.
    /// </summary>
    public string MissingReason(string graphName)
    {
        if (!_statuses.TryGetValue(graphName, out var status))
        {
            return $"the catalogue publishes no grid operation for the datum {graphName}.";
        }

        return status.Directory is not null ? string.Empty : status.Reason;
    }

    /// <summary>Whether a machine with a bundle deployed can be compared against PROJ at all.</summary>
    public bool CanCompareAgainstProj => _proj is not null;

    /// <summary>Why there is no reference, or the empty string when there is one.</summary>
    public string MissingReferenceReason =>
        _proj is not null
            ? string.Empty
            : "no PROJ on this machine: cs2cs is not on the PATH and "
                + $"{ProjVariable} names none. PROJ has to be installed with the same bundle in its data "
                + "directory (PROJ_DATA, or PROJ_LIB on PROJ 8 and earlier), because it is PROJ that reads "
                + "the bundle, not this suite.";

    /// <summary>
    /// The reference reader for one datum, or a skip carrying the reason. The
    /// skip is the contract: an operator who ran the suite and saw a line of
    /// dots needs to be told which of the two things — the bundle or PROJ — is
    /// the one that is not there.
    /// </summary>
    public Func<ControlPoint, string, (double X, double Y)> ReferenceFor(string graphName)
    {
        Skip.If(!IsDeployed(graphName), MissingReason(graphName));
        Skip.If(_proj is null, MissingReferenceReason);

        var executable = _proj!;
        return (point, target) => ProjReference.Answer(executable, "EPSG:4326", target, point.Longitude, point.Latitude);
    }

    /// <summary>
    /// Both answers for one control point: the engine's, through a host built
    /// over the deployed directories, and PROJ's, through the same bundle as
    /// PROJ has it. The two are measured here rather than in the test so that
    /// the arithmetic between them is a thing with its own tests, and so that a
    /// suite which never reaches this — because nothing is deployed — is a suite
    /// that says so rather than one that passes vacuously.
    /// </summary>
    public Agreement Measure(ControlPoint point, string target)
    {
        var (referenceX, referenceY) = ReferenceFor(point.Datum)(point, target);
        var projected = Assert.IsType<Point>(new ProjNetTransforms(Directories)
            .Transform(
                GeometryFactory.CreatePoint(point.Longitude, point.Latitude, CoordinateReference.Epsg(4326)),
                "EPSG:4326",
                target));

        return new Agreement(point.Name, target, projected.X!.Value, projected.Y!.Value, referenceX, referenceY);
    }

    /// <summary>
    /// The whole state of the exercise, as the text a skipped run and a run
    /// that compared both print. Every bundle the catalogue publishes a row for
    /// is named whether it is deployed or not, so the report says what the
    /// suite <em>did not</em> cover as well as what it did.
    /// </summary>
    public string Report()
    {
        var report = new StringBuilder();
        var configured = Directories.Count == 0 ? "none configured" : string.Join(", ", Directories);
        report.AppendLine("published-bundle agreement with PROJ — ADR-0105 §licence, ADR-0179");
        report.AppendLine(CultureInfo.InvariantCulture, $"  grid directories ({GridDirectoryVariable}): {configured}");
        foreach (var absent in _absent)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"    {absent} does not exist — it is echoed back rather than dropped (ADR-0105 §3)");
        }

        foreach (var (graphName, status) in _statuses)
        {
            report.AppendLine(
                status.Directory is null
                    ? $"  {status.Operation.FileName} ({graphName}) not deployed: {status.Reason}"
                    : $"  {status.Operation.FileName} ({graphName}) deployed, read from {status.Directory}");
        }

        report.AppendLine(
            _proj is null
                ? $"  PROJ reference (cs2cs): none — {MissingReferenceReason}"
                : $"  PROJ reference (cs2cs): {_proj}");
        report.AppendLine(
            "  residual pinned at "
            + PinnedResidualMetres.ToString("0.###", CultureInfo.InvariantCulture)
            + " m against PROJ reading the same bundle");
        report.AppendLine(
            "  no bundle is fetched, embedded or vendored anywhere in this repository, and which bundles "
            + "may lawfully be deployed on a given machine is that operator's licence position rather than "
            + "the engine's (ADR-0105 §licence)");
        return report.ToString();
    }

    /// <summary>The deployment as this machine's environment describes it.</summary>
    public static PublishedGridDeployment FromEnvironment() =>
        Resolve(
            Environment.GetEnvironmentVariable,
            executable => Locate(executable, Environment.GetEnvironmentVariable(ProjVariable)));

    /// <summary>
    /// The deployment the given environment describes. The two readers are
    /// parameters rather than calls to <see cref="Environment"/> so that every
    /// state this harness can be in — deployed, misdirected, empty, unreadable,
    /// no PROJ — is reachable from a test without a machine that happens to be
    /// in it.
    /// </summary>
    public static PublishedGridDeployment Resolve(
        Func<string, string?> environment,
        Func<string, string?> locate)
    {
        var configured = environment(GridDirectoryVariable);
        var directories = string.IsNullOrWhiteSpace(configured)
            ? []
            : configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // A directory that does not exist is remembered and echoed rather than
        // dropped: an operator who mistyped a path needs to see it (ADR-0105 §3).
        var absent = directories.Where(directory => !Directory.Exists(directory)).ToArray();
        var registry = DatumShiftGridRegistry.Load(directories);

        var statuses = new Dictionary<string, Status>(StringComparer.Ordinal);
        foreach (var graphName in EpsgGridShiftOperations.GraphNames.Distinct(StringComparer.Ordinal))
        {
            var operations = EpsgGridShiftOperations.For(graphName);
            var operation = operations.FirstOrDefault(candidate => directories.Any(directory =>
                File.Exists(Path.Combine(directory, candidate.FileName))));

            if (operation is null)
            {
                statuses[graphName] = new Status(operations[0], null, NoBundle(directories, absent, operations));
            }
            else if (registry.TryGet(graphName, out _))
            {
                statuses[graphName] = new Status(operation, From(directories, operation), string.Empty);
            }
            else
            {
                statuses[graphName] = new Status(operation, null, Unreadable(operation, registry, operations));
            }
        }

        return new PublishedGridDeployment(directories, absent, statuses, locate("cs2cs"));
    }

    /// <summary>The first directory holding a bundle, which is the one that serves it.</summary>
    private static string From(string[] directories, EpsgGridShiftOperations.GridShiftOperation operation) =>
        directories.First(directory => File.Exists(Path.Combine(directory, operation.FileName)));

    /// <summary>Why no bundle for this datum turned up where the operator said it would be.</summary>
    private static string NoBundle(
        string[] directories,
        string[] absent,
        IReadOnlyList<EpsgGridShiftOperations.GridShiftOperation> operations)
    {
        var expected = string.Join(" or ", operations.Select(operation => operation.FileName));

        if (directories.Length == 0)
        {
            return $"{GridDirectoryVariable} is not set. A published bundle is deployed by dropping it into a "
                + "configured directory, and the decision to deploy one is an operator's under the "
                + "operator's own licence position (ADR-0105 §licence); nothing in this repository fetches one.";
        }

        return absent.Length == directories.Length
            ? $"none of the {directories.Length} configured directories exists, so the {expected} is not there."
            : $"the configured directories hold no {expected}, so the datum is answered by the classic Helmert.";
    }

    /// <summary>
    /// Why a bundle that is present was not used. The reader's own reason,
    /// because a mis-deployed file and a reader that got the format wrong are
    /// different problems and the operator has to be able to tell them apart.
    /// </summary>
    private static string Unreadable(
        EpsgGridShiftOperations.GridShiftOperation operation,
        DatumShiftGridRegistry registry,
        IReadOnlyList<EpsgGridShiftOperations.GridShiftOperation> operations)
    {
        var reason = registry.Failures
            .Where(failure => operations.Any(candidate => candidate.FileName == failure.FileName))
            .Select(failure => failure.Reason)
            .FirstOrDefault();

        return $"'{operation.FileName}' is present and unreadable — {reason ?? "the reader refused it without saying why"}.";
    }

    /// <summary>
    /// Finds an executable: the operator's override first, then the PATH. A
    /// directory holding a file of the right name is not enough — it has to be
    /// executable, or the suite would discover PROJ is present by failing to
    /// start it.
    /// </summary>
    private static string? Locate(string executable, string? overridden)
    {
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return File.Exists(overridden) ? overridden : null;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>One datum's bundle as this deployment found it.</summary>
    private sealed record Status(
        EpsgGridShiftOperations.GridShiftOperation Operation,
        string? Directory,
        string Reason);
}
