using Spatial.Transformations.ProjNet.Grids;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The NADCON reader, against bytes the test writes itself
/// (<see cref="NadconFixture"/>). As with the NTv2 reader, nothing here needs a
/// published bundle: the format is the thing under test, and the expected
/// shifts are the arithmetic the fixture chose.
/// <para>
/// The byte layout these tests pin is <em>this engine's reading</em> of the
/// NADCON container, not a bundle fetched from NOAA: ADR-0105 §licence keeps
/// published grids out of the tests, so a published NADCON bundle is not
/// available to check a field width against. Agreement with a real bundle is
/// SpatialEngine-yt2's to establish, and a bundle this reader refuses is
/// refused loudly rather than half-read.
/// </para>
/// <para>
/// The fixture writes what a deployed pair holds, which is positive west
/// throughout its longitudes (ADR-0181): the header's edges and the .los
/// half's shifts. A <c>NadconFixture.Constant</c> value is therefore the shift
/// <em>the file states</em>, and the expected results below are stated in
/// degrees east-positive, which is where the conversion belongs. The two tests
/// named for the convention are the ones that pin it; the rest use a pair of
/// one grid and would pass either way round, which is why they are not what
/// this file's reader defect is tested by.
/// </para>
/// </summary>
public sealed class NadconGridReaderTests
{
    /// <summary>A tenth of a second of arc is about three metres of latitude.</summary>
    private const double ArcSecond = 1.0 / 3600.0;

    /// <summary>The accuracy the caller publishes for a NADCON grid, in metres.</summary>
    private const double StatedAccuracy = 0.15;

    /// <summary>The two halves of the pair, by the names a catalogue row carries.</summary>
    private const string LatitudeName = "conus.las";

    private const string LongitudeName = "conus.los";

    private static (byte[] Latitude, byte[] Longitude) Pair(
        NadconFixture.Grid latitude,
        NadconFixture.Grid longitude) =>
        (NadconFixture.ToBytes(latitude, longitude: false), NadconFixture.ToBytes(longitude, longitude: true));

    [Fact]
    public void The_block_and_the_identity_are_read_off_the_header()
    {
        var (latitude, longitude) = NadconFixture.ToPair(
            NadconFixture.Constant("CONUS", 24.0, 26.0, -84.0, -82.0, 1.0, 1.0, 1.0f));

        Assert.True(NadconGridReader.TryRead(latitude, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error), error);
        Assert.Null(error);
        Assert.Equal("CONUS", grids![0].Name);
        Assert.Equal(GridFormat.Nadcon, grids![0].Format);
        Assert.Equal(24.0, grids[0].YMin, 9);
        Assert.Equal(26.0, grids[0].YMax, 9);
        Assert.Equal(-84.0, grids[0].XMin, 9);
        Assert.Equal(-82.0, grids[0].XMax, 9);
    }

    [Fact]
    public void The_containers_positive_west_longitudes_are_read_as_positive_east()
    {
        // The reproduction, stated in the container's own terms. A NADCON
        // deployment states every longitude positive west, in the header and
        // in the .los half, because the method was built for NAD27's
        // positive-west longitudes: EPSG's own note on operations 1241, 1243
        // and 15864 says the NADCON method "expects longitudes positive west"
        // while the datums it serves are positive east, and PROJ 9.8.1 negates
        // the header extents and the node values for the .los half for
        // exactly that reason. So a .los holding +1.5 at every node is a
        // shift of one and a half seconds *towards the west*, and a header
        // stating 84 and 82 is the block whose eastern edge is 82°W.
        //
        // Read as positive east, the coordinate moves the other way and the
        // block lands in the eastern hemisphere, where there is nothing to
        // interpolate: a wrong coordinate rather than a missing one.
        //
        // The fixture takes what the file states, so the .los half is asked
        // for -1.5 to hold +1.5 on disk, and the .las half — positive north,
        // like every other latitude here — is asked for +1.5.
        var latitudes = NadconFixture.Constant("CONUS", 24.0, 26.0, -84.0, -82.0, 1.0, 1.0, 1.5f);
        var longitudes = NadconFixture.Constant("CONUS", 24.0, 26.0, -84.0, -82.0, 1.0, 1.0, -1.5f);
        var (latitude, longitude) = Pair(latitudes, longitudes);

        Assert.True(NadconGridReader.TryRead(latitude, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error), error);

        // The header's longitudes are east-positive once read, so the block is
        // the one the datums are tabulated over.
        Assert.Equal(-84.0, grids![0].XMin, 9);
        Assert.Equal(-82.0, grids[0].XMax, 9);

        // The .las half is unchanged by the convention and the .los half is
        // not: +1.5 on disk is 1.5 seconds towards the west, so the longitude
        // moves down and the latitude up.
        Assert.True(grids[0].TryShiftForward(-83.5, 25.5, out var shiftedLatitude, out var shiftedLongitude));
        Assert.Equal(25.5 + (1.5 * ArcSecond), shiftedLatitude, 9);
        Assert.Equal(-83.5 - (1.5 * ArcSecond), shiftedLongitude, 9);
    }

    [Fact]
    public void A_longitude_shift_either_side_of_the_east_positive_zero_both_survive()
    {
        // The container states a sign either way round, so a reader that
        // negates one file's numbers is right for both and a reader that
        // ignored the convention is wrong for both. This is the whole of the
        // defect: a NAD27 point west of Greenwich needs its longitude moved
        // by whatever the file tabulates, and which way that is has to come
        // out of the same negation rather than out of the sign of the number.
        var latitudes = NadconFixture.Constant("CONUS", 24.0, 26.0, -84.0, -82.0, 1.0, 1.0, 1.5f);
        var longitudes = NadconFixture.Constant("CONUS", 24.0, 26.0, -84.0, -82.0, 1.0, 1.0, 1.5f);
        var (latitude, longitude) = Pair(latitudes, longitudes);

        Assert.True(NadconGridReader.TryRead(latitude, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error), error);
        Assert.True(grids![0].TryShiftForward(-83.5, 25.5, out var shiftedLatitude, out var shiftedLongitude));

        // The .los holds -1.5 here, so the shift is towards the east.
        Assert.Equal(25.5 + (1.5 * ArcSecond), shiftedLatitude, 9);
        Assert.Equal(-83.5 + (1.5 * ArcSecond), shiftedLongitude, 9);
    }

    [Fact]
    public void The_published_format_is_the_standards_own_name()
    {
        // A client reading a findTransformations listing is told which standard
        // served the shift (ADR-0105 §1), and the enum member is not the
        // spelling it wants to see.
        Assert.Equal("NADCON", GridFormats.Standard(GridFormat.Nadcon));
        Assert.Equal("NTv2", GridFormats.Standard(GridFormat.Ntv2));
    }

    [Fact]
    public void A_constant_shift_moves_the_coordinate_by_that_shift()
    {
        var (latitude, longitude) = NadconFixture.ToPair(
            NadconFixture.Constant("CONUS", 24.0, 26.0, -84.0, -82.0, 1.0, 1.0, 1.5f));

        Assert.True(NadconGridReader.TryRead(latitude, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error), error);
        Assert.True(grids![0].TryShiftForward(-83.5, 25.5, out var shiftedLatitude, out var shiftedLongitude));
        Assert.Equal(25.5 + (1.5 * ArcSecond), shiftedLatitude, 9);
        Assert.Equal(-83.5 + (1.5 * ArcSecond), shiftedLongitude, 9);
    }

    [Fact]
    public void The_latitude_shifts_come_from_the_las_file_and_the_longitude_from_the_los()
    {
        // The two halves of a NADCON pair are separate containers, and the
        // whole difference between them and an NTv2 bundle is which component
        // each file carries. A reader that took both from one of them would
        // answer with the same number twice.
        var latitudes = NadconFixture.Constant("CONUS", 24.0, 26.0, -84.0, -82.0, 1.0, 1.0, 1.0f);
        var longitudes = NadconFixture.Constant("CONUS", 24.0, 26.0, -84.0, -82.0, 1.0, 1.0, 3.0f);
        var (latitude, longitude) = Pair(latitudes, longitudes);

        Assert.True(NadconGridReader.TryRead(latitude, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error), error);
        Assert.True(grids![0].TryShiftForward(-83.5, 25.5, out var shiftedLatitude, out var shiftedLongitude));
        Assert.Equal(25.5 + (1.0 * ArcSecond), shiftedLatitude, 9);
        Assert.Equal(-83.5 + (3.0 * ArcSecond), shiftedLongitude, 9);
    }

    [Fact]
    public void A_point_on_a_node_takes_that_nodes_own_shift()
    {
        // NADCON stores its rows from the north down, so a reader that took
        // them in file order would put the block the wrong way up. The shift here
        // rises with the row index counted from the south, so the nodes asked
        // for below answer with the shifts of the first and second row whichever
        // way up the file stored them: read in file order, the reader would
        // take the wrong row for both.
        var nodes = new float[4, 3];
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                nodes[row, column] = (float)row;
            }
        }

        var grid = new NadconFixture.Grid("CONUS", 40.0, 43.0, -80.0, -78.0, 1.0, 1.0, nodes);
        var (latitude, longitude) = NadconFixture.ToPair(grid);

        Assert.True(NadconGridReader.TryRead(latitude, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error), error);

        // One row in from each edge, since ADR-0105 §7 puts a point on the
        // outermost row outside the grid: there is no cell around it.
        Assert.True(grids![0].TryShiftForward(-80.0, 41.0, out var shiftedLatitude, out var shiftedLongitude), error);
        Assert.Equal(41.0 + (1.0 * ArcSecond), shiftedLatitude, 9);
        Assert.Equal(-80.0 + (1.0 * ArcSecond), shiftedLongitude, 9);
        Assert.True(grids[0].TryShiftForward(-80.0, 42.0, out shiftedLatitude, out shiftedLongitude), error);
        Assert.Equal(42.0 + (2.0 * ArcSecond), shiftedLatitude, 9);
        Assert.Equal(-80.0 + (2.0 * ArcSecond), shiftedLongitude, 9);
        Assert.False(grids[0].Covers(-80.0, 43.0), "the northernmost row of nodes is the far edge of the last cell, not a point inside one.");
    }

    [Fact]
    public void A_pair_whose_halves_describe_different_blocks_is_refused()
    {
        // The two files are joined on their headers, so a pair that does not
        // agree about the block it covers is not a grid: taking one half's
        // shifts against the other's extents would tabulate over ground the
        // shifts were never computed for.
        var latitudes = NadconFixture.Constant("CONUS", 24.0, 26.0, -84.0, -82.0, 1.0, 1.0, 1.0f);
        var longitudes = NadconFixture.Constant("CONUS", 24.0, 26.0, -84.0, -81.0, 1.0, 1.0, 1.0f);
        var (latitude, longitude) = Pair(latitudes, longitudes);

        Assert.False(NadconGridReader.TryRead(latitude, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error), error);
        Assert.Empty(grids);
        Assert.NotNull(error);
        Assert.Contains(".las", error, StringComparison.Ordinal);
        Assert.Contains(".los", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_header_whose_counts_disagree_with_its_increments_is_refused()
    {
        // The header states the block twice over — as edges with increments,
        // and as node counts — and this reader reads both and cross-checks
        // them, so a header that cannot account for its own block yields a
        // reason and no grid rather than a block of the wrong shape.
        var (latitude, longitude) = NadconFixture.ToPair(
            NadconFixture.Constant("CONUS", 24.0, 26.0, -84.0, -82.0, 1.0, 1.0, 1.0f));

        // The fixture's header states three rows; nine cannot be the same
        // block at a one-degree increment.
        var inconsistent = ReplaceBlockNumber(latitude, index: 6, "9");
        Assert.False(NadconGridReader.TryRead(inconsistent, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error), error);
        Assert.Empty(grids);
        Assert.NotNull(error);
        Assert.Contains("9", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_nadcon_grid_is_refused_with_a_reason()
    {
        var notAGrid = System.Text.Encoding.ASCII.GetBytes("this is not a grid file".PadRight(256, '\0'));

        Assert.False(NadconGridReader.TryRead(notAGrid, "notes.las", notAGrid, "notes.los", StatedAccuracy, out var grids, out var error), error);
        Assert.Empty(grids);
        Assert.NotNull(error);
    }

    [Fact]
    public void A_file_that_stops_inside_its_shift_block_is_refused()
    {
        var (latitude, longitude) = NadconFixture.ToPair(
            NadconFixture.Constant("CONUS", 24.0, 26.0, -84.0, -82.0, 1.0, 1.0, 1.0f));

        var truncated = latitude[..(latitude.Length - 7)];
        Assert.False(NadconGridReader.TryRead(truncated, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error), error);
        Assert.Empty(grids);
        Assert.NotNull(error);
    }

    [Fact]
    public void A_file_named_but_not_present_is_refused_with_a_reason()
    {
        Assert.False(NadconGridReader.TryRead("/does/not/exist.las", "/does/not/exist.los", StatedAccuracy, out var grids, out var error), error);
        Assert.Empty(grids);
        Assert.NotNull(error);
        Assert.Contains(".las", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_accuracy_is_the_figure_the_catalogue_publishes_rather_than_the_worst_node()
    {
        // NTv2 tabulates an accuracy per node and the grid is only as good as
        // its worst one. A NADCON shift record holds the shift alone, so
        // there is nothing in the file to read and the accuracy the operation
        // is published with is the one its row carries.
        var (latitude, longitude) = NadconFixture.ToPair(
            NadconFixture.Constant("CONUS", 24.0, 26.0, -84.0, -82.0, 1.0, 1.0, 1.0f));

        Assert.True(NadconGridReader.TryRead(latitude, LatitudeName, longitude, LongitudeName, 0.42, out var grids, out var error), error);
        Assert.Equal(0.42, grids![0].AccuracyMetres, 9);
    }

    /// <summary>
    /// Replaces one of the ten numbers the sub-grid header states, in the
    /// bytes. The numbers run across the header records that follow the
    /// identity record, so they are gathered, changed and written back over
    /// the same region.
    /// </summary>
    private static byte[] ReplaceBlockNumber(byte[] bytes, int index, string value)
    {
        // Records zero to two are the overview and record three is the
        // sub-grid's identity; the block is what follows it.
        const int ValueLength = NadconFixture.RecordLength - 8;
        var first = (NadconFixture.OverviewRecordCount + 1) * NadconFixture.RecordLength;
        var count = (NadconFixture.SubGridRecordCount - 1) * NadconFixture.RecordLength;

        var text = string.Empty;
        for (var offset = first; offset < first + count; offset += NadconFixture.RecordLength)
        {
            text += System.Text.Encoding.ASCII.GetString(bytes, offset + 8, ValueLength);
        }

        var tokens = text.Split([' ', '\0'], StringSplitOptions.RemoveEmptyEntries);
        tokens[index] = value;
        var rewritten = string.Join(' ', tokens);
        Assert.True(rewritten.Length <= count, "the test's replacement has to fit the records it is written into.");

        var patched = (byte[])bytes.Clone();
        Array.Fill(patched, (byte)'\0', first, count);
        System.Text.Encoding.ASCII.GetBytes(rewritten).CopyTo(patched.AsSpan(first));
        return patched;
    }
}