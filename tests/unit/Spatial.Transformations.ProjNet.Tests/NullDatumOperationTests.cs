using Spatial.Contracts.TransformationSearch;
using Spatial.Transformations.ProjNet.Grids;
using static Spatial.Transformations.ProjNet.Tests.GraphInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// A registered operation that moves nothing is still an operation
/// (ADR-0163). EPSG registers each of ETRS89 (1149), NAD83 (1188), RGF93
/// v1 (1671) and NZGD2000 (1565) against WGS 84 as a shift whose parameters
/// are all zero, at a published accuracy of 1.0, 4.0, 1.0 and 1.0 m. That is
/// the registry stating that the datum realises WGS 84 without moving, and
/// the accuracy is the registry stating what that realisation is worth — a
/// metre for New Zealand, four for NAD83.
/// <para>
/// ADR-0087 §2 read the identity as "there is no operation to publish", so
/// every one of those pairs came back <c>[]</c> and the accuracy column was
/// stated for rows the graph never used. ADR-0163 amends that: the registered
/// null operation is published as a candidate carrying its published
/// accuracy, and nothing is published only where no leg is registered, the
/// pair is the same datum, or the two datums share no ground.
/// </para>
/// </summary>
public sealed class NullDatumOperationTests : IDisposable
{
    private static HelmertParameters Parameters(CrsTransformation candidate) => Assert.Single(candidate.Steps).Parameters!;

    /// <summary>The temporary directory a deployed bundle is written into, taken
    /// away again when the test class is finished with it (ADR-0105 §licence: a
    /// grid is deployed, never vendored).</summary>
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

    /// <summary>A registry over a directory holding an NTv2 bundle for NAD83,
    /// built rather than fetched: every published grid is third-party data, so
    /// a test that depended on one would be a test that depended on the
    /// network. The bundle's bytes are a constant shift, so the accuracy a
    /// candidate is published with is the fixture's arithmetic.</summary>
    private DatumShiftGridRegistry DeployedNad83Grid()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"spatialengine-nulldatum-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        File.WriteAllBytes(
            Path.Combine(directory, "NAD83_to_WGS84_NTv2.gsb"),
            Ntv2Fixture.ToBytes(Ntv2Fixture.Constant(
                "NAD83",
                24.0,
                49.0,
                -124.0,
                -66.0,
                0.25,
                0.25,
                new Ntv2Fixture.Shift(1.0f, 1.0f, 0.05f, 0.05f))));
        return DatumShiftGridRegistry.Load([directory]);
    }

    [Fact]
    public void WGS84_to_ETRS89_publishes_the_null_operation_EPSG_registers()
    {
        // EPSG:1149 "ETRS89 to WGS 84 (1)", method EPSG:9603 geocentric
        // translations with tx = ty = tz = 0, accuracy 1.0 m, over extent 4755.
        var registered = EpsgDatumOperations.ReadForTest("European Terrestrial Reference System 1989")!;

        var candidate = Assert.Single(Search("EPSG:4326", "EPSG:4258"));

        Assert.Equal("WGS84_To_ETRS89_Geocentric_Translation", candidate.Name);
        Assert.Equal(registered.AccuracyMetres, candidate.AccuracyMetres, 9);
        Assert.False(candidate.Approximate, "a registered operation is not an approximation of one.");
        var parameters = Parameters(candidate);
        Assert.Equal([0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0],
            [parameters.Tx, parameters.Ty, parameters.Tz, parameters.Rx, parameters.Ry, parameters.Rz, parameters.ScalePpm]);
        // Valid over the ground both datums apply, which is the whole of
        // ETRS89's registered extent.
        Assert.Equal(registered.AreaOfUse.Boxes, candidate.AreaOfUse.Boxes);
    }

    [Fact]
    public void WGS84_to_NAD83_publishes_the_null_operation_at_the_accuracy_the_registry_states()
    {
        // EPSG:1188 "NAD83 to WGS 84 (1)", accuracy 4.0 m. The whole point of
        // publishing it: four metres is what the registry says the two
        // realisations disagree by, and a client told nothing is told less
        // than the registry knows.
        var candidate = Assert.Single(Search("EPSG:4326", "EPSG:4269"));

        Assert.Equal("WGS84_To_NAD83_Geocentric_Translation", candidate.Name);
        Assert.Equal(4.0, candidate.AccuracyMetres, 9);
        Assert.Equal("Geocentric translations (geog2D domain)", candidate.Method);
        Assert.Equal(-172.54, Assert.Single(candidate.AreaOfUse.Boxes).XMin, 9);
    }

    [Fact]
    public void WGS84_to_NZGD2000_publishes_the_null_operation_over_both_halves_of_a_wrapped_extent()
    {
        // EPSG:1565 "NZGD2000 to WGS 84 (1)", accuracy 1.0 m, over extent 1175
        // — the wrapped extent ADR-0111 split into two rectangles. The node
        // was always in the graph; the operation it stands for is now
        // published over the ground the registry registered it over.
        var candidate = Assert.Single(Search("EPSG:4326", "EPSG:4167"));

        Assert.Equal("WGS84_To_NZGD2000_Geocentric_Translation", candidate.Name);
        Assert.Equal(1.0, candidate.AccuracyMetres, 9);
        Assert.Equal(
            [new CrsAreaOfUseBox(160.6, -55.95, 180.0, -25.88), new CrsAreaOfUseBox(-180.0, -55.95, -171.2, -25.88)],
            candidate.AreaOfUse.Boxes);
    }

    [Fact]
    public void The_null_operation_is_the_only_candidate_a_null_pair_publishes()
    {
        // Three names for a shift that moves nothing would be three claims
        // about the same ground. The concatenated form would carry no steps at
        // all — both legs are null — and the reduced form would restate the
        // same three zero translations with a broader area and the same
        // accuracy, which is a wider claim at the same price.
        foreach (var target in new[] { "EPSG:4258", "EPSG:4269", "EPSG:4167", "EPSG:4171" })
        {
            var candidates = Search("EPSG:4326", target);

            var candidate = Assert.Single(candidates);
            Assert.EndsWith("_Geocentric_Translation", candidate.Name, StringComparison.Ordinal);
            Assert.Single(candidate.Steps);
        }
    }

    [Fact]
    public void A_null_operation_is_published_over_the_ground_the_registry_registered_it_over()
    {
        // The filter is still a filter, and the Chathams are inside extent
        // 1175 — so the New Zealand operation is published for a request over
        // ground west of the antimeridian, which is the candidate that used to
        // have to be built from a stand-in shift to exist at all.
        var candidates = Search("EPSG:4326", "EPSG:4167", NewArea(-172.5, -44.5, -171.5, -43.5));

        Assert.Equal("WGS84_To_NZGD2000_Geocentric_Translation", Assert.Single(candidates).Name);
    }

    [Fact]
    public void Ground_no_null_operation_is_registered_over_publishes_nothing()
    {
        // The filter, from the other side: middle of the Pacific is in neither
        // registered extent, so the operation is not returned for it rather
        // than returned and left to the client to distrust.
        Assert.Empty(Search("EPSG:4326", "EPSG:4167", NewArea(-140.0, -30.0, -130.0, -20.0)));
    }

    [Fact]
    public void A_null_pair_that_shares_no_ground_still_publishes_nothing()
    {
        // ETRS89 and NAD83 both move nothing, and Europe and North America
        // share no ground, so the registered operation between them exists but
        // is valid nowhere: an empty intersection drops the candidate, which
        // is ADR-0087 §4 and is not what this record amends.
        Assert.Empty(Search("EPSG:4258", "EPSG:26910"));
    }

    [Fact]
    public void The_same_datum_needs_no_transformation()
    {
        // Same datum, not two datums that agree: there is no registered
        // operation between a datum and itself, so the honest answer is the
        // empty one.
        Assert.Empty(Search("EPSG:4326", "EPSG:3857"));
        Assert.Empty(Search("EPSG:4326", "EPSG:4326"));
    }

    [Fact]
    public void A_null_pair_is_symmetric()
    {
        var forward = Assert.Single(Search("EPSG:4326", "EPSG:4167"));
        var reverse = Assert.Single(Search("EPSG:4167", "EPSG:4326"));

        Assert.Equal(forward.Name, reverse.Name);
        Assert.Equal(forward.AccuracyMetres, reverse.AccuracyMetres, 9);
        Assert.Equal(forward.AreaOfUse, reverse.AreaOfUse);
        Assert.False(Assert.Single(reverse.Steps).TransformForward);
    }

    [Fact]
    public void A_null_pair_with_a_grid_deployed_ranks_the_grid_first_and_still_states_the_registry()
    {
        // The null operation is the registry's answer; a deployed bundle is a
        // better one. Both are published, ranked by stated accuracy, so the
        // path the engine applies is the first candidate and the registry's
        // own figure is still there for the ground the grid does not cover
        // (ADR-0105, ADR-0107).
        var bare = Assert.Single(Search("EPSG:4326", "EPSG:4269"));
        Assert.Equal(4.0, bare.AccuracyMetres, 9);

        var deployed = Search("EPSG:4326", "EPSG:4269", DeployedNad83Grid());

        var grid = Assert.Single(deployed, candidate => candidate.Name != bare.Name);
        Assert.Contains(grid.Steps, step => step.GridShift is not null);
        Assert.True(grid.AccuracyMetres < bare.AccuracyMetres);
        Assert.Contains(deployed, candidate => candidate.Name == bare.Name && candidate.AccuracyMetres == 4.0);
    }
}
