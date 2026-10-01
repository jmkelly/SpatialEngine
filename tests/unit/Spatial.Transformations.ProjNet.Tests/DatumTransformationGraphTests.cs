using Spatial.Contracts;
using Spatial.Contracts.TransformationSearch;
using Spatial.Transformations.ProjNet.Grids;
using static Spatial.Transformations.ProjNet.Tests.GraphInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The datum-transformation graph (ADR-0087): operations are first-class
/// values carrying their own parameters, an area of use and a stated
/// accuracy, searched by datum pair and ranked best-accuracy first. These
/// tests pin the graph's contract — candidates, symmetry, ranking and area
/// filtering — independently of any interop surface.
/// </summary>
public sealed class DatumTransformationGraphTests
{
    [Fact]
    public void The_same_datum_needs_no_transformation()
    {
        // WGS 84 geographic to WGS 84 Pseudo-Mercator: same datum, no step.
        var candidates = Search("EPSG:4326", "EPSG:3857");

        Assert.Empty(candidates);
    }

    [Fact]
    public void A_null_datum_pair_that_shares_no_ground_publishes_nothing()
    {
        // ETRS89 and NAD83 are distinct datums that both realise WGS 84 with
        // a zero shift, so the composed operation is the identity and the
        // registry registers one between them — but Europe and North America
        // share no ground, so that operation is valid nowhere and ADR-0087
        // §4's empty intersection drops it. That is not the identity rule:
        // against WGS 84 each of these datums publishes the registered null
        // operation at the accuracy EPSG states (ADR-0163).
        var candidates = Search("EPSG:4258", "EPSG:26910");

        Assert.Empty(candidates);
    }

    [Fact]
    public void A_cross_datum_search_returns_ranked_candidates_carrying_their_parameters()
    {
        var candidates = Search("EPSG:4326", "EPSG:27700");

        Assert.True(candidates.Count >= 2, "a datum step must offer more than one operation.");
        // The engine applies the composed geocentric shift, so it ranks
        // first: one step, carrying the catalogue's OSGB36 parameters read
        // in the requested direction (WGS 84 to OSGB 36 is the inverse of
        // the datum's own shift to WGS 84).
        var applied = candidates[0];
        Assert.Single(applied.Steps);
        Assert.True(applied.Steps[0].TransformForward);
        var parameters = Helmert(applied.Steps[0]);
        // The exact inverse of a Helmert is not the negated forward
        // translation - the 20 ppm scale and the sub-arcsecond rotations move
        // it by centimetres - so these are the composed values, not the
        // catalogue's TOWGS84 read backwards.
        Assert.Equal(-446.456, parameters.Tx, 3);
        Assert.Equal(125.161, parameters.Ty, 3);
        Assert.Equal(-542.072, parameters.Tz, 3);
        Assert.Equal(20.489, parameters.ScalePpm, 3);
        Assert.Equal(-0.150, parameters.Rx, 3);
        // EPSG:1314 "OSGB36 to WGS 84 (6)" states 2.0 m, and the search's
        // accuracy is that datum's alone, the WGS 84 pivot contributing none.
        Assert.Equal(2.0, applied.AccuracyMetres, 3);
        Assert.False(applied.Approximate);

        // Ranked best-accuracy first.
        for (var index = 1; index < candidates.Count; index++)
        {
            Assert.True(
                candidates[index - 1].AccuracyMetres <= candidates[index].AccuracyMetres,
                $"{candidates[index - 1].Name} ({candidates[index - 1].AccuracyMetres}) must rank before " +
                $"{candidates[index].Name} ({candidates[index].AccuracyMetres}).");
        }
    }

    [Fact]
    public void The_reduced_candidate_states_what_dropping_the_rotations_costs()
    {
        var candidates = Search("EPSG:4326", "EPSG:27700");

        var reduced = candidates[^1];
        Assert.EndsWith("_Geocentric_Translation", reduced.Name, StringComparison.Ordinal);
        var parameters = Helmert(reduced.Steps[0]);
        Assert.Equal(0.0, parameters.Rx, 9);
        Assert.Equal(0.0, parameters.ScalePpm, 9);
        // OSGB36's rotations are nearly an arcsecond, so dropping them costs
        // metres - the reduced operation ranks last, as it must.
        Assert.True(reduced.AccuracyMetres > 5.0, $"expected a degraded accuracy, got {reduced.AccuracyMetres} m.");
    }

    [Fact]
    public void The_concatenated_candidate_chains_through_WGS84()
    {
        // NAD83 and OSGB36 do not overlap, so the direct operation is valid
        // nowhere and only the path through the world datum's own domain
        // survives - here a single step, because a modern datum has no shift
        // of its own to concatenate.
        var candidates = Search("EPSG:4269", "EPSG:4277");

        var chained = Assert.Single(candidates);
        Assert.EndsWith("_via_WGS84", chained.Name, StringComparison.Ordinal);
        var step = Assert.Single(chained.Steps);
        Assert.Equal("WGS84_To_OSGB36_Helmert", step.Name);
        Assert.True(step.TransformForward);
        Assert.True(chained.Approximate);

        // Asked the other way round it is the same operation, run backwards.
        var reverse = Assert.Single(Search("EPSG:4277", "EPSG:4269"));
        Assert.Equal(chained.Name, reverse.Name);
        Assert.Equal(step.Name, Assert.Single(reverse.Steps).Name);
        Assert.False(Assert.Single(reverse.Steps).TransformForward);
    }

    [Fact]
    public void Two_shifted_datums_concatenate_two_steps()
    {
        // The catalogue carries one shifted datum today, so the two-step
        // form is pinned on the graph itself: each datum's own step against
        // WGS 84, in the requested order, alongside the composed operation.
        var alpha = Shifted("Alpha", 100.0, 3.0, -9.0, 40.0, -3.0, 52.0);
        var beta = Shifted("Beta", 400.0, 2.0, -4.0, 41.0, 2.0, 55.0);

        var candidates = DatumTransformationGraph.Search(alpha, beta, DatumShiftGridRegistry.Empty);

        var composed = candidates[0];
        Assert.Equal("Alpha_To_Beta_Helmert", composed.Name);
        var chained = candidates[1];
        Assert.Equal(2, chained.Steps.Count);
        Assert.Equal("Alpha_To_WGS84_Helmert", chained.Steps[0].Name);
        Assert.Equal("WGS84_To_Beta_Helmert", chained.Steps[1].Name);
        // The composed operation is valid where both datums apply, the
        // concatenated one wherever either does. The union is a set of
        // rectangles rather than their bounding box: the two extents here
        // overlap, so the concatenated operation names the ground of both
        // and not the sea between them, and a wrapped extent makes the
        // bounding box the whole planet (ADR-0111).
        Assert.Equal(
            [new CrsAreaOfUseBox(-9.0, 40.0, -3.0, 52.0), new CrsAreaOfUseBox(-4.0, 41.0, 2.0, 55.0)],
            chained.AreaOfUse.Boxes);
        Assert.Equal(41.0, GraphInvoker.Only(composed.AreaOfUse).YMin, 6);
        Assert.Equal(52.0, GraphInvoker.Only(composed.AreaOfUse).YMax, 6);
        Assert.Equal(40.0, chained.AreaOfUse.Boxes[0].YMin, 6);
        Assert.Equal(55.0, chained.AreaOfUse.Boxes[1].YMax, 6);
    }

    private static DatumNode Shifted(string name, double tx, double accuracy, double xMin, double yMin, double xMax, double yMax) =>
        new(name, name, new HelmertParameters(tx, 0, 0, 0, 0, 0, 0), accuracy, CrsAreaOfUse.One(name, xMin, yMin, xMax, yMax));

    [Fact]
    public void The_search_is_symmetric()
    {
        var forward = Search("EPSG:4326", "EPSG:27700");
        var reverse = Search("EPSG:27700", "EPSG:4326");

        Assert.Equal(forward.Count, reverse.Count);
        for (var index = 0; index < forward.Count; index++)
        {
            // The same operations, asked for the other way round: the same
            // names, the same parameters, the steps marked as running
            // backwards and in reverse order.
            Assert.Equal(forward[index].Name, reverse[index].Name);
            Assert.Equal(
                forward[index].Steps.Select(step => step.Name).Reverse(),
                reverse[index].Steps.Select(step => step.Name));
            Assert.All(reverse[index].Steps, step => Assert.False(step.TransformForward));
            Assert.Equal(forward[index].Steps[0].Parameters, reverse[index].Steps[0].Parameters);
        }
    }
    [Fact]
    public void An_extent_outside_every_area_of_use_filters_the_candidates()
    {
        // The OSGB36 shift is valid over Great Britain, so a request centred
        // on France is served only by the path valid everywhere.
        var anywhere = Search("EPSG:4326", "EPSG:27700");
        var inBritain = Search("EPSG:4326", "EPSG:27700", NewArea(-2.0, 51.5, 0.0, 53.0));
        var inFrance = Search("EPSG:4326", "EPSG:27700", NewArea(2.0, 45.0, 6.0, 48.0));

        Assert.Equal(3, anywhere.Count);
        Assert.Equal(3, inBritain.Count);
        var inFranceChain = Assert.Single(inFrance);
        Assert.Contains("via_WGS84", inFranceChain.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transformation_is_found_for_every_projected_crs_of_its_datum()
    {
        // The graph is keyed on datums, not CRSs: a projected CRS finds the
        // same operations as its geographic parent.
        var geographic = Search("EPSG:4277", "EPSG:4326");
        var projected = Search("EPSG:27700", "EPSG:4326");

        Assert.Equal(geographic.Select(candidate => candidate.Name), projected.Select(candidate => candidate.Name));
    }

    [Fact]
    public void An_unreadable_crs_is_an_invalid_argument_not_an_empty_search()
    {
        // "No transformation needed" and "no such CRS" are different answers,
        // and only the first is a result.
        var unknown = Assert.Throws<SpatialException>(() => Search("EPSG:4326", "EPSG:4267"));
        Assert.Equal(SpatialException.InvalidArguments, unknown.Code);
        Assert.Contains("4267", unknown.Message);

        var malformed = Assert.Throws<SpatialException>(() => Search("EPSG:4326", "not-an-identity"));
        Assert.Equal(SpatialException.InvalidArguments, malformed.Code);
        Assert.Contains("CRS identity", malformed.Message);
    }

    [Fact]
    public void The_search_honours_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Search(new CrsTransformationQuery("EPSG:4326", "EPSG:27700"), cancelled.Token));
    }

    /// <summary>
    /// A step's Helmert parameters, asserting the step is a Helmert one: a grid
    /// step publishes no seven parameters, and a test that read them without
    /// saying which kind of step it was holding would pass for the wrong
    /// reason.
    /// </summary>
    private static HelmertParameters Helmert(CrsTransformationStep step)
    {
        Assert.Null(step.GridShift);
        return Assert.IsType<HelmertParameters>(step.Parameters);
    }
}
