using Spatial.Contracts.TransformationSearch;
using Spatial.Transformations.ProjNet.Grids;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// An area of use that crosses the antimeridian (ADR-0111). EPSG publishes
/// such extents by giving a west bound in the east and an east bound in the
/// west: extent 1175 "New Zealand" is 160.6E to 171.2W and extent 2157 the
/// Aleutians is 164.84E to 164.84W. A box read that way is empty, so an area
/// of use that is one box either drops the datum out of the graph or clips
/// the east bound at 180 and leaves the ground inside the registered extent
/// west of the antimeridian uncovered.
/// <para>
/// The decision under test: an area of use is a <em>set</em> of in-range
/// boxes, a wrapped extent is the two boxes it actually is, and the set
/// algebra is ordinary rectangle algebra. A wrapped operand therefore can
/// never be mistaken for an empty one, and it can never min/max its way into
/// a result that poisons whatever composed with it.
/// </para>
/// <para>
/// The operations are read off the catalogue rather than restated, so a row
/// that is transcribed wrongly fails here too. NZGD2000 carries no TOWGS84
/// shift in its WKT definition, so the service cannot reach the graph for it
/// at all; that gap is a different one (the operation row's parameters, not
/// its extent) and the node is built here so this record tests the area of
/// use and nothing else.
/// </para>
/// </summary>
public class WrappedAreaOfUseTests
{
    private const string NewZealandDatum = "New Zealand Geodetic Datum 2000";

    /// <summary>Where the registered New Zealand extent lies west of the
    /// antimeridian — a strip of the registered extent the clipped box used to
    /// leave out, and ground the direct operation is valid over.</summary>
    private static CrsAreaOfUse Chathams() => GraphInvoker.NewArea(-172.5, -44.5, -171.5, -43.5);

    private static CrsAreaOfUse ExtentOf(string datumName) =>
        EpsgDatumOperations.ReadForTest(datumName)!.AreaOfUse;

    private static CrsAreaOfUse NewZealand() => ExtentOf(NewZealandDatum);

    private static CrsAreaOfUse World() => ProjEpsgCatalog.WorldDatum().AreaOfUse;

    private static CrsAreaOfUse NorthAmerica() => ExtentOf("North American Datum 1983");

    private static CrsAreaOfUse France() => ExtentOf("Reseau Geodesique Francais 1993");

    /// <summary>The New Zealand datum as the graph runs on: the shift, the
    /// accuracy and the area of use of the row that stands for it. The shift
    /// is a stand-in — NZGD2000 carries no TOWGS84 in its WKT, which is a gap
    /// about the operation's parameters and not about its extent, and this
    /// record tests the extent.</summary>
    private static DatumNode NewZealandNode()
    {
        var row = EpsgDatumOperations.ReadForTest(NewZealandDatum)!;
        return new DatumNode(
            row.GraphName,
            NewZealandDatum,
            new HelmertParameters(1.0, 2.0, 3.0, 0, 0, 0, 0),
            row.AccuracyMetres,
            row.AreaOfUse);
    }

    /// <summary>The candidates the graph offers from WGS 84 to New Zealand
    /// over a given piece of ground. Asked in that order because the graph
    /// builds its candidates in the catalogued datum order, and the WGS 84
    /// pivot sorts first.</summary>
    private static IReadOnlyList<CrsTransformation> OverNewZealand(CrsAreaOfUse areaOfInterest) =>
        DatumTransformationGraph.Search(
            ProjEpsgCatalog.WorldDatum(),
            NewZealandNode(),
            DatumShiftGridRegistry.Empty,
            areaOfInterest);

    [Fact]
    public void The_new_zealand_extent_is_read_as_the_two_boxes_it_is()
    {
        // Read verbatim from extent 1175: 160.6E to 171.2W, 55.95S to 25.88S.
        // That is not a box and it is not empty either — it is two boxes,
        // split at the antimeridian, each a real rectangle in range.
        var boxes = NewZealand().Boxes;

        Assert.Equal(2, boxes.Count);
        Assert.Equal(new CrsAreaOfUseBox(160.6, -55.95, 180.0, -25.88), boxes[0]);
        Assert.Equal(new CrsAreaOfUseBox(-180.0, -55.95, -171.2, -25.88), boxes[1]);
        Assert.All(boxes, box =>
        {
            Assert.True(box.XMin <= box.XMax, "every box of an area of use is a rectangle.");
            Assert.True(box.YMin <= box.YMax, "every box of an area of use is a rectangle.");
            Assert.InRange(box.XMin, -180.0, 180.0);
            Assert.InRange(box.XMax, -180.0, 180.0);
        });
    }

    [Fact]
    public void New_zealand_is_in_the_graph_over_the_ground_west_of_the_antimeridian()
    {
        // The reproduction. A client asking about the Chatham Islands — inside
        // the registered extent 1175, west of the antimeridian — used to come
        // back with nothing at all, because the area of use was the clipped
        // box 160.6E to 180 and no candidate covered the strip.
        var candidates = OverNewZealand(Chathams());

        var direct = candidates[0];
        Assert.Equal("WGS84_To_NZGD2000_Helmert", direct.Name);
        Assert.False(direct.Approximate);

        // The published area of use is the whole registered extent, both
        // halves of it — not the eastern half.
        Assert.Equal(2, direct.AreaOfUse.Boxes.Count);
        Assert.Contains(direct.AreaOfUse.Boxes, box => box.XMin <= -171.5 && box.XMax >= -172.5);
    }

    [Fact]
    public void A_wrapped_area_of_use_keeps_its_western_half_when_intersected_with_the_world()
    {
        // The poisoning case. Intersecting by min/max took the eastern box's
        // XMin against the western box's XMax and produced an empty result,
        // so the *whole* of New Zealand was declared to share no ground with
        // WGS 84 — the datum dropped out of the graph rather than the part of
        // it that does not overlap. The intersection of a wrapped area with
        // the world is the wrapped area.
        var intersection = DatumTransformationGraph.Intersect(NewZealand(), World());

        Assert.Equal(2, intersection.Boxes.Count);
        Assert.Contains(intersection.Boxes, box => box.XMin <= -171.5);
        Assert.Contains(intersection.Boxes, box => box.XMin >= 160.6);
    }

    [Fact]
    public void Two_extents_that_meet_only_across_the_antimeridian_still_intersect()
    {
        // The other direction: neither operand is the world, and the only
        // ground they share is the strip west of the seam.
        var intersection = DatumTransformationGraph.Intersect(NewZealand(), Chathams());

        var box = Assert.Single(intersection.Boxes);
        Assert.Equal(-172.5, box.XMin, 9);
        Assert.Equal(-171.5, box.XMax, 9);
    }

    [Fact]
    public void A_wrapped_area_of_use_does_not_widen_a_union_it_takes_part_in()
    {
        // Union by min/max over a wrapped operand spans the world: the
        // western box's -180 meets the eastern box's 180, and the bounding
        // box of the two is the planet. The union of a wrapped extent and a
        // European one is New Zealand plus Europe, not everywhere.
        var union = DatumTransformationGraph.Union(NewZealand(), France());

        Assert.Equal(3, union.Boxes.Count);
        Assert.All(union.Boxes, box => Assert.True(box.XMin > 0.0 || box.XMax < 180.0));
        Assert.Contains(union.Boxes, box => box.XMin <= -171.5);
        Assert.Contains(union.Boxes, box => box.XMin >= -9.86 && box.XMax <= 10.38);
    }

    [Fact]
    public void A_union_drops_the_boxes_already_covered()
    {
        // The world contains New Zealand, so the union of the two is the
        // world — one box, not three. Concatenating without reducing would
        // publish a redundant area, and a client drawing it would draw New
        // Zealand twice.
        var union = DatumTransformationGraph.Union(NewZealand(), World());

        var box = Assert.Single(union.Boxes);
        Assert.Equal(-180.0, box.XMin, 9);
        Assert.Equal(180.0, box.XMax, 9);
    }

    [Fact]
    public void An_extent_with_no_shared_ground_with_another_datum_is_dropped()
    {
        // The failure case that must survive: New Zealand and North America
        // really do share no ground, so the direct operation between them is
        // dropped and only a path that applies somewhere does survive. An
        // empty intersection is a real answer, not a defect in the algebra.
        var intersection = DatumTransformationGraph.Intersect(NewZealand(), NorthAmerica());

        Assert.Empty(intersection.Boxes);
    }

    [Fact]
    public void An_area_of_interest_no_candidate_covers_filters_out_the_local_candidates()
    {
        // The filter is a filter, and it filters the local ones: ground in the
        // middle of the Pacific is not in New Zealand's registered extent, so
        // the direct operation — valid only where both datums apply — is not
        // returned for it. The concatenated path still is, because it is valid
        // wherever either step applies and WGS 84 applies everywhere, which is
        // exactly the difference between the two the graph draws.
        var candidates = OverNewZealand(GraphInvoker.NewArea(-140.0, -30.0, -130.0, -20.0));

        Assert.DoesNotContain(candidates, candidate => !candidate.Approximate);
        Assert.All(candidates, candidate => Assert.EndsWith("_via_WGS84", candidate.Name, StringComparison.Ordinal));
    }

    [Fact]
    public void An_area_of_interest_may_itself_be_a_set()
    {
        // A caller asking about ground either side of the seam asks about two
        // boxes. Neither half is empty and neither is the whole interest, so
        // a search has to hold a candidate if it covers either — which is
        // what the split buys over a single inverted box.
        var candidates = OverNewZealand(
            new CrsAreaOfUse(
                "the requested extent of interest",
                [new CrsAreaOfUseBox(170.0, -47.0, 178.0, -40.0), new CrsAreaOfUseBox(-180.0, -47.0, -172.0, -40.0)]));

        Assert.NotEmpty(candidates);
    }

    [Fact]
    public void A_cancelled_search_over_a_wrapped_extent_still_cancels()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => GraphInvoker.Search(new CrsTransformationQuery("EPSG:4326", "EPSG:27700", Chathams()), cts.Token));
    }
}
