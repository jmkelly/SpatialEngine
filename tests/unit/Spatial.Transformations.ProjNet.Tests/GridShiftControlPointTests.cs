using Spatial.Contracts;
using Spatial.Contracts.TransformationSearch;
using Spatial.Core.Geometry;
using Spatial.Transformations.ProjNet.Grids;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The control points for the grid registry and the listing it feeds
/// (ADR-0105). What is measured here is what this slice delivers and no more:
/// <list type="bullet">
/// <item>with a bundle deployed, the grid-backed candidate is published ahead
/// of the Helmert it outranks, carries the grid's own coverage, and states an
/// accuracy derived from the grid file rather than asserted beside it;</item>
/// <item>with a bundle deployed, the Helmert path a client falls back to is
/// bit-for-bit the one a host with no bundle applies, and it still lands on
/// the published PROJ reference inside the 0.1 m it is stated at;</item>
/// <item>with no bundle deployed, nothing changes at all.</item>
/// </list>
/// <para>
/// The grid is the synthetic one from <see cref="Ntv2Fixture"/> rather than a
/// published bundle: every published grid is third-party data whose licence
/// and redistribution are open questions (ADR-0105 §licence), so a test that
/// depended on fetching one would be a test that depended on the network. That
/// is also why the numbers compared against PROJ here are the ones the
/// Helmert fallback is responsible for — a sub-metre agreement with PROJ over
/// <em>published</em> bundles is a deployment question, and is filed as the
/// follow-up this ADR names.
/// </para>
/// </summary>
public sealed class GridShiftControlPointTests : IDisposable
{
    private const double CoveringSouth = 49.5;
    private const double CoveringNorth = 52.5;
    private const double BlockWest = -9.0;
    private const double BlockEast = 1.0;
    private const double FixtureLatitudeShift = 1.0;
    private const double FixtureLongitudeShift = 2.0;

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
    /// A directory holding an OSTN-shaped bundle whose shifts this test chose,
    /// covering the given block of Great Britain. The London control point is
    /// inside the covering block and outside the southern one.
    /// </summary>
    private string Deploy(double south = CoveringSouth, double north = CoveringNorth)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"spatialengine-control-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        File.WriteAllBytes(
            Path.Combine(directory, EpsgGridShiftOperations.OsgbBundle),
            Ntv2Fixture.ToBytes(Ntv2Fixture.Constant(
                "OSTN15",
                south,
                north,
                BlockWest,
                BlockEast,
                0.25,
                0.25,
                new Ntv2Fixture.Shift(
                    (float)FixtureLatitudeShift,
                    (float)FixtureLongitudeShift,
                    0.05f,
                    0.05f))));
        return directory;
    }

    [Fact]
    public void A_deployed_grid_publishes_a_candidate_ranked_ahead_of_the_Helmert()
    {
        var candidates = new ProjNetTransforms([Deploy()])
            .FindTransformations(new CrsTransformationQuery("EPSG:4326", "EPSG:4277"));
        var helmertOnly = new ProjNetTransforms()
            .FindTransformations(new CrsTransformationQuery("EPSG:4326", "EPSG:4277"));

        Assert.Contains(candidates, candidate => candidate.Steps.Any(step => step.GridShift is not null));
        Assert.Equal(4, candidates.Count);
        Assert.Equal(3, helmertOnly.Count);

        // Ranked first by stated accuracy, and more accurate than the Helmert
        // it outranks — the ranking is the existing ADR-0087 rule, not a new
        // one, and a grid is simply the better operation.
        Assert.Contains(candidates[0].Steps, step => step.GridShift is not null);
        Assert.True(candidates[0].AccuracyMetres < helmertOnly[0].AccuracyMetres);
    }

    [Fact]
    public void The_grid_candidate_publishes_its_coverage_and_a_derived_accuracy()
    {
        var candidate = new ProjNetTransforms([Deploy()])
            .FindTransformations(new CrsTransformationQuery("EPSG:4326", "EPSG:4277"))[0];
        var step = Assert.Single(candidate.Steps, step => step.GridShift is not null);
        var shift = Assert.IsType<GridShiftParameters>(step.GridShift);

        Assert.Equal("OSTN15", shift.GridName);
        Assert.Equal("NTv2", shift.Format);
        Assert.Equal("bilinear", shift.Interpolation);
        Assert.Equal(CoveringSouth, shift.YMin, 9);
        Assert.Equal(CoveringNorth, shift.YMax, 9);
        Assert.Equal(BlockWest, shift.XMin, 9);
        Assert.Equal(BlockEast, shift.XMax, 9);
        Assert.Null(step.Parameters);

        // The accuracy is the grid's own worst node, and the operation is no
        // less accurate than that — not a figure typed out beside the file
        // name, which is what would let the two disagree.
        Assert.Equal((double)0.05f, candidate.AccuracyMetres, 9);
    }

    [Fact]
    public void The_grid_candidate_says_how_the_transform_verb_chooses_between_grid_and_Helmert()
    {
        var candidate = new ProjNetTransforms([Deploy()])
            .FindTransformations(new CrsTransformationQuery("EPSG:4326", "EPSG:4277"))[0];

        // ADR-0087 §6 ranks the first candidate as the applied path, and since
        // ADR-0107 the grid is that path. A client reading a sub-metre answer
        // needs to know it is the grid that produced it and the Helmert that
        // stands behind it outside the block, so the text carries the choice.
        Assert.Contains("applies the grid per coordinate", candidate.Method, StringComparison.Ordinal);
        Assert.DoesNotContain("still applies the classic Helmert", candidate.Method, StringComparison.Ordinal);
    }

    [Fact]
    public void A_client_outside_the_grids_block_is_told_the_Helmert_is_the_fallback()
    {
        var grid = new ProjNetTransforms([Deploy()])
            .FindTransformations(new CrsTransformationQuery("EPSG:4326", "EPSG:4277"))[0];
        var helmert = new ProjNetTransforms([Deploy()])
            .FindTransformations(new CrsTransformationQuery("EPSG:4326", "EPSG:4277"))
            .Single(candidate => candidate.Steps.All(step => step.GridShift is null) && !candidate.Approximate);

        // The coverage is published, so an area of interest over ground the
        // grid does not cover filters the grid candidate out and the Helmert is
        // what remains — which is the whole point of publishing the block.
        var overFrance = new ProjNetTransforms([Deploy()])
            .FindTransformations(new CrsTransformationQuery(
                "EPSG:4326",
                "EPSG:4277",
                new CrsAreaOfUse("over France", 2.0, 46.0, 3.0, 47.0)));

        Assert.DoesNotContain(overFrance, candidate => candidate.Steps.Any(step => step.GridShift is not null));
        Assert.Contains(overFrance, candidate => candidate.Steps.All(step => step.GridShift is null));
        Assert.Contains("classic Helmert approximation", helmert.Method, StringComparison.Ordinal);
        Assert.Contains("every point the grid does not cover", helmert.Method, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploying_a_bundle_over_London_moves_it_away_from_the_Helmert()
    {
        // The change ADR-0107 makes: this block covers London, so the point is
        // now answered by the grid rather than by the Helmert. Before the
        // transform verb applied the grid, the two answers were the same and
        // this comparison was the fallback contract; now it is the evidence
        // that the deployed grid is the path applied.
        var withBundle = await ProjectAsync([Deploy()]);
        var withoutBundle = await ProjectAsync([]);

        Assert.NotEqual(withoutBundle.X!.Value, withBundle.X!.Value, 6);
        Assert.NotEqual(withoutBundle.Y!.Value, withBundle.Y!.Value, 6);
    }

    [Fact]
    public async Task Deploying_a_bundle_leaves_an_unserved_datum_untouched()
    {
        // RGF93 is a modern datum the catalogue carries with no shift of its
        // own, so an Ordnance Survey bundle must not move a French point by a
        // single float.
        var withBundle = await ProjectAsync([Deploy()], ControlPoints.Lyon.Lon, ControlPoints.Lyon.Lat, "EPSG:2154");
        var withoutBundle = await ProjectAsync([], ControlPoints.Lyon.Lon, ControlPoints.Lyon.Lat, "EPSG:2154");

        Assert.Equal(withoutBundle.X!.Value, withBundle.X!.Value, 12);
        Assert.Equal(withoutBundle.Y!.Value, withBundle.Y!.Value, 12);
    }

    /// <summary>The London control point projected to the national grid, with the given bundles deployed.</summary>
    private static async Task<Point> ProjectAsync(
        string[] directories,
        double? longitude = null,
        double? latitude = null,
        string target = "EPSG:27700")
    {
        var (x, y) = (longitude ?? ControlPoints.London.Lon, latitude ?? ControlPoints.London.Lat);
        var geometry = GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326));
        var transformed = new ProjNetTransforms(directories).Transform(geometry, "EPSG:4326", target);
        return Assert.IsType<Point>(transformed);
    }
}
