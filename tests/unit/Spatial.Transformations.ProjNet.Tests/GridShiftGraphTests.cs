using Spatial.Contracts.TransformationSearch;
using Spatial.Core.Geometry;
using Spatial.Transformations.ProjNet.Grids;
using static Spatial.Transformations.ProjNet.Tests.GraphInvoker;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// What a grid bundle changes about the transformation graph (ADR-0105): a
/// grid-backed candidate is published ahead of the Helmert one it replaces,
/// carrying the grid's own parameters and a derived accuracy, and where no
/// bundle is deployed the Helmert candidate stands and says in its method
/// string that it is the fallback and what it costs.
/// </summary>
public sealed class GridShiftGraphTests : IDisposable
{
    /// <summary>
    /// The accuracy the fixture's nodes carry, as the reader sees it. An NTv2
    /// node stores its accuracy as a single-precision float, so the published
    /// figure inherits that precision rather than the test's own double — which
    /// is worth pinning, because a grid is only as accurate as its worst node
    /// and the file is the authority on what that is.
    /// </summary>
    private const double OstnGridAccuracy = (double)0.05f;


    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// A registry over a directory holding an OSTN-shaped bundle for
    /// Ordnance Survey 1936, or the empty registry when none is deployed.
    /// </summary>
    private static DatumShiftGridRegistry Registry(bool deployed)
    {
        if (!deployed)
        {
            return DatumShiftGridRegistry.Empty;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"spatialengine-graph-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var grid = Ntv2Fixture.Constant(
            "OSTN15",
            49.75,
            50.75,
            -9.0,
            0.0,
            0.25,
            0.25,
            new Ntv2Fixture.Shift(1.0f, 2.0f, (float)OstnGridAccuracy, (float)OstnGridAccuracy));
        File.WriteAllBytes(
            Path.Combine(directory, EpsgGridShiftOperations.OsgbBundle),
            Ntv2Fixture.ToBytes(grid));
        return DatumShiftGridRegistry.Load([directory]);
    }

    private static IReadOnlyList<CrsTransformation> Search(string source, string target, bool deployed) =>
        GraphInvoker.Search(source, target, datumShiftGridRegistry: deployed ? Registry(deployed: true) : DatumShiftGridRegistry.Empty);

    [Fact]
    public void With_no_bundle_deployed_the_search_is_the_one_it_was()
    {
        var candidates = Search("EPSG:4326", "EPSG:4277", deployed: false);

        Assert.Equal(3, candidates.Count);
        Assert.All(candidates, candidate => Assert.DoesNotContain(candidate.Steps, step => step.GridShift is not null));
    }

    [Fact]
    public void A_deployed_bundle_publishes_a_grid_backed_candidate()
    {
        var candidates = Search("EPSG:4326", "EPSG:4277", deployed: true);

        var grid = Assert.Single(candidates, candidate => candidate.Steps.Any(step => step.GridShift is not null));
        Assert.Contains("OSTN15", grid.Name, StringComparison.Ordinal);
        Assert.Contains("grid", grid.Method, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_grid_candidate_ranks_ahead_of_the_Helmert_it_replaces()
    {
        var deployed = Search("EPSG:4326", "EPSG:4277", deployed: true);
        var bare = Search("EPSG:4326", "EPSG:4277", deployed: false);

        // ADR-0087's ranking is by stated accuracy, and the grid is the more
        // accurate operation, so it comes first — which is also what makes
        // it the operation the engine applies (ADR-0087 §the first candidate).
        Assert.Contains(deployed[0].Steps, step => step.GridShift is not null);
        Assert.True(deployed[0].AccuracyMetres < bare[0].AccuracyMetres);
        Assert.Equal(4, deployed.Count);
    }

    [Fact]
    public void The_grid_step_publishes_the_grid_its_coverage_and_how_it_interpolates()
    {
        var grid = Search("EPSG:4326", "EPSG:4277", deployed: true)[0];
        var step = Assert.Single(grid.Steps, step => step.GridShift is not null);
        var parameters = step.GridShift!;

        Assert.Equal("OSTN15", parameters.GridName);
        Assert.Equal(EpsgGridShiftOperations.OsgbBundle, parameters.FileName);
        Assert.Equal("NTv2", parameters.Format);
        Assert.Equal("bilinear", parameters.Interpolation);
        Assert.Equal(49.75, parameters.YMin, 6);
        Assert.Equal(50.75, parameters.YMax, 6);
        Assert.Equal(-9.0, parameters.XMin, 6);
        Assert.Equal(0.0, parameters.XMax, 6);
        Assert.Null(step.Parameters);
    }

    [Fact]
    public void A_pure_grid_candidate_is_as_accurate_as_the_grids_worst_node()
    {
        var grid = Search("EPSG:4326", "EPSG:4277", deployed: true)[0];

        // WGS 84 is the pivot and needs no shift of its own, so the whole
        // operation is the grid and the grid's own worst-node accuracy is the
        // whole of the stated accuracy. Nothing is added to it for free.
        Assert.Equal(OstnGridAccuracy, grid.AccuracyMetres, 9);
    }

    [Fact]
    public void A_candidate_whose_other_leg_is_the_Helmert_says_it_is_an_approximation()
    {
        // The served catalogue has only one shifted datum (ADR-0027), so no
        // served pair can put a grid and a Helmert on the two legs at once.
        // The graph is exercised on its own nodes instead, where the other
        // leg's registered accuracy is a number the test chose.
        var candidates = DatumTransformationGraph.Search(
            Shifted("Alpha", 100.0, 2.0, -10.0, 40.0, 10.0, 60.0),
            Shifted("OSGB36", 446.456, 3.0, -8.82, 49.79, 1.92, 60.94),
            Registry(deployed: true));

        var grid = Assert.Single(candidates, candidate => candidate.Steps.Any(step => step.GridShift is not null));
        Assert.Contains(grid.Steps, step => step.GridShift is not null);
        Assert.Contains(grid.Steps, step => step.Parameters is not null);
        Assert.True(grid.Approximate, "one of this candidate's legs is still the Helmert approximation.");
    }

    [Fact]
    public void The_grid_candidates_accuracy_is_derived_from_the_grid_not_asserted()
    {
        // The grid's own worst-node accuracy replaces the served datum's
        // registered Helmert accuracy, and the surviving Helmert leg still
        // combines with it in quadrature — the same rule ADR-0087 applies to
        // two Helmerts, and nothing here is a hand-typed figure.
        var candidates = DatumTransformationGraph.Search(
            Shifted("Alpha", 100.0, 2.0, -10.0, 40.0, 10.0, 60.0),
            Shifted("OSGB36", 446.456, 3.0, -8.82, 49.79, 1.92, 60.94),
            Registry(deployed: true));

        var grid = Assert.Single(candidates, candidate => candidate.Steps.Any(step => step.GridShift is not null));
        var expected = Math.Sqrt((OstnGridAccuracy * OstnGridAccuracy) + (2.0 * 2.0));
        Assert.Equal(expected, grid.AccuracyMetres, 9);
    }

    [Fact]
    public void With_no_bundle_the_Helmert_candidate_states_that_it_is_the_fallback()
    {
        var bare = Search("EPSG:4326", "EPSG:4277", deployed: false);

        var direct = bare[0];
        Assert.Contains("Helmert approximation", direct.Method, StringComparison.Ordinal);
        Assert.Contains("no NTv2 grid is deployed", direct.Method, StringComparison.Ordinal);
        Assert.Contains($"{bare[0].AccuracyMetres:F1} m", direct.Method, StringComparison.Ordinal);
    }

    [Fact]
    public void With_a_bundle_deployed_the_Helmert_candidate_says_it_is_not_the_one_applied()
    {
        var deployed = Search("EPSG:4326", "EPSG:4277", deployed: true);
        // The direct Helmert candidate: single step, not a concatenation and
        // not the reduced form, so the only one of the three that is not
        // itself marked approximate.
        var helmert = Assert.Single(deployed, candidate =>
            candidate.Steps.All(step => step.GridShift is null)
            && candidate.Steps[0].Parameters is not null
            && !candidate.Approximate);

        Assert.Contains("Helmert approximation", helmert.Method, StringComparison.Ordinal);
        Assert.Contains("not the operation applied", helmert.Method, StringComparison.Ordinal);
    }

    [Fact]
    public void A_projected_CRS_on_a_grid_backed_datum_gets_the_same_candidates_as_its_geographic_parent()
    {
        var geographic = Search("EPSG:4326", "EPSG:4277", deployed: true);
        var projected = Search("EPSG:4326", "EPSG:27700", deployed: true);

        // The graph is keyed on datums (ADR-0087), so a projected CRS resolves
        // through its geographic base and the grid candidate appears for both.
        Assert.Equal(
            geographic.Select(candidate => candidate.Steps[0].GridShift?.GridName),
            projected.Select(candidate => candidate.Steps[0].GridShift?.GridName));
    }

    [Fact]
    public void A_reversed_search_publishes_the_same_grid_running_backwards()
    {
        var forward = Search("EPSG:4326", "EPSG:4277", deployed: true);
        var reverse = Search("EPSG:4277", "EPSG:4326", deployed: true);

        Assert.Equal(
            forward.Select(candidate => candidate.Name),
            reverse.Select(candidate => candidate.Name));
        var gridStep = reverse[0].Steps[0];
        Assert.NotNull(gridStep.GridShift);
        Assert.False(gridStep.TransformForward, "the search is symmetric: the same operation, marked as running in reverse.");
    }

    [Fact]
    public void A_datum_with_no_published_grid_is_untouched_by_a_deployment()
    {
        var bare = Search("EPSG:4326", "EPSG:4171", deployed: false);
        var deployed = Search("EPSG:4326", "EPSG:4171", deployed: true);

        // RGF93 has no grid operation row, so deploying an Ordnance Survey
        // bundle changes nothing about how France is transformed.
        Assert.Equal(bare.Select(candidate => candidate.Name), deployed.Select(candidate => candidate.Name));
    }

    [Fact]
    public void A_search_outside_the_grids_block_keeps_the_Helmert_candidates()
    {
        // The candidate is published with the grid's own coverage, so an area
        // of interest off the block filters it out and the Helmert candidates
        // are what is left — which is the point of publishing the coverage.
        var overFrance = GraphInvoker.Search(
            new CrsTransformationQuery("EPSG:4326", "EPSG:4277", NewArea(2.0, 46.0, 3.0, 47.0)),
            Registry(deployed: true),
            CancellationToken.None);

        Assert.DoesNotContain(overFrance, candidate => candidate.Steps.Any(step => step.GridShift is not null));
        Assert.NotEmpty(overFrance);
    }

    /// <summary>
    /// A synthetic shifted datum node, so a test can name a grid-served datum
    /// and a Helmert-served one in the same pair — which the served catalogue
    /// cannot supply, having only one shifted datum.
    /// </summary>
    private static DatumNode Shifted(string name, double translation, double accuracy, double xMin, double yMin, double xMax, double yMax) =>
        new(
            name,
            name,
            new HelmertParameters(translation, 0, 0, 0, 0, 0, 0),
            accuracy,
            new CrsAreaOfUse(name, xMin, yMin, xMax, yMax));
}
