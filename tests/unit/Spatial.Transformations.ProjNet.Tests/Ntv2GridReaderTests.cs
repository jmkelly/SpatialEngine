using Spatial.Transformations.ProjNet.Grids;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The NTv2 reader, against bytes the test writes itself
/// (<see cref="Ntv2Fixture"/>). Nothing here needs a published grid bundle:
/// the format is the thing under test, and the expected shifts are the
/// arithmetic the fixture chose.
/// </summary>
public sealed class Ntv2GridReaderTests
{
    /// <summary>A tenth of a second of arc is about three metres of latitude.</summary>
    private const double ArcSecond = 1.0 / 3600.0;

    private static Ntv2Fixture.Shift Shift(double latitudeSeconds, double longitudeSeconds) =>
        new((float)latitudeSeconds, (float)longitudeSeconds, 0.05f, 0.05f);

    [Fact]
    public void The_overview_and_sub_grid_header_are_read()
    {
        var grid = Ntv2Fixture.Constant("OSTN15", 49.75, 50.75, -9.0, 0.0, 0.25, 0.25, Shift(1.0, 2.0));

        Assert.True(Ntv2GridReader.TryRead(Ntv2Fixture.ToBytes(grid), "OSTN15_osgb_02.gsb", out var grids, out var error));
        Assert.Null(error);
        Assert.Equal("OSTN15", grids![0].Name);
        Assert.Equal("OSTN15_osgb_02.gsb", grids![0].FileName);
        Assert.Equal(GridFormat.Ntv2, grids![0].Format);
        Assert.Equal(49.75, grids![0].YMin, 9);
        Assert.Equal(50.75, grids![0].YMax, 9);
        Assert.Equal(-9.0, grids![0].XMin, 9);
        Assert.Equal(0.0, grids![0].XMax, 9);
    }

    [Fact]
    public void A_constant_shift_moves_the_coordinate_by_that_shift()
    {
        var grid = Ntv2Fixture.Constant("OSTN15", 49.75, 50.75, -9.0, 0.0, 0.25, 0.25, Shift(1.0, 2.0));
        Assert.True(Ntv2GridReader.TryRead(Ntv2Fixture.ToBytes(grid), "grid.gsb", out var grids, out _));

        // Well inside the block, so the interpolated value is the constant one.
        Assert.True(grids![0].TryShiftForward(-0.1276, 50.0, out var latitude, out var longitude));
        Assert.Equal(50.0 + 1.0 * ArcSecond, latitude, 12);
        Assert.Equal(-0.1276 + 2.0 * ArcSecond, longitude, 12);
    }

    [Fact]
    public void A_point_on_a_node_takes_that_nodes_own_shift()
    {
        // The longitude shift is the node's column, so bilinear interpolation at
        // a node must return the node's value exactly rather than a blend of
        // its neighbours: this is the check that pins the interpolation to the
        // fractional position within the cell, which is the convention the
        // reference implementation uses.
        var nodes = new Ntv2Fixture.Shift[2, 3];
        for (var column = 0; column < 3; column++)
        {
            nodes[0, column] = Shift(0.0, column);
            nodes[1, column] = Shift(0.0, column);
        }

        var grid = new Ntv2Fixture.Grid("N", "A", "B", 50.0, 51.0, 0.0, 2.0, 1.0, 1.0, nodes);
        Assert.True(Ntv2GridReader.TryRead(Ntv2Fixture.ToBytes(grid), "grid.gsb", out var grids, out _));

        Assert.True(grids![0].TryShiftForward(1.0, 50.0, out var latitude, out var longitude));
        Assert.Equal(50.0, latitude, 12);
        Assert.Equal(1.0 + 1.0 * ArcSecond, longitude, 12);
    }

    [Fact]
    public void The_shift_is_interpolated_bilinearly_within_the_cell()
    {
        // A three-by-three block over latitudes 50..52 and longitudes 0..2, so
        // the point (0.5, 50.5) sits at the centre of the lower-left cell. The
        // four corner shifts there are 0, 2, 2 and 2 seconds of arc, so the
        // bilinear blend is their mean: one second. Snapping to the nearest
        // node would answer 0 or 2 instead, which is what makes this the check
        // that the interpolation blends rather than picks.
        var nodes = new Ntv2Fixture.Shift[3, 3];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                nodes[row, column] = Shift(2.0 * row, 2.0 * column);
            }
        }

        var grid = new Ntv2Fixture.Grid("N", "A", "B", 50.0, 52.0, 0.0, 2.0, 1.0, 1.0, nodes);
        Assert.True(Ntv2GridReader.TryRead(Ntv2Fixture.ToBytes(grid), "grid.gsb", out var grids, out _));

        Assert.True(grids![0].TryShiftForward(0.5, 50.5, out var latitude, out var longitude));
        Assert.Equal(50.5 + 1.0 * ArcSecond, latitude, 12);
        Assert.Equal(0.5 + 1.0 * ArcSecond, longitude, 12);
    }

    [Fact]
    public void A_grid_stored_from_north_to_south_still_shifts_the_right_way()
    {
        // Real bundles exist with N_LAT below S_LAT, so the file walks the rows
        // downwards. The shift must come out the same sign either way.
        var nodes = new Ntv2Fixture.Shift[2, 3];
        for (var row = 0; row < 2; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                nodes[row, column] = Shift(1.0, 2.0);
            }
        }

        var descending = new Ntv2Fixture.Grid("DESC", "A", "B", 51.0, 50.0, 0.0, 2.0, 1.0, 1.0, nodes);
        Assert.True(Ntv2GridReader.TryRead(Ntv2Fixture.ToBytes(descending), "grid.gsb", out var grids, out _));

        Assert.Equal(50.0, grids![0].YMin, 9);
        Assert.Equal(51.0, grids![0].YMax, 9);
        Assert.True(grids![0].TryShiftForward(0.5, 50.5, out var latitude, out var longitude));
        Assert.Equal(50.5 + 1.0 * ArcSecond, latitude, 12);
        Assert.Equal(0.5 + 2.0 * ArcSecond, longitude, 12);
    }

    [Fact]
    public void The_inverse_shift_undoes_the_forward_one()
    {
        var nodes = new Ntv2Fixture.Shift[3, 3];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                nodes[row, column] = Shift(0.5 * row, 0.25 * column);
            }
        }

        var grid = new Ntv2Fixture.Grid("N", "A", "B", 50.0, 52.0, 0.0, 2.0, 1.0, 1.0, nodes);
        Assert.True(Ntv2GridReader.TryRead(Ntv2Fixture.ToBytes(grid), "grid.gsb", out var grids, out _));

        const double Longitude = 0.7;
        const double Latitude = 50.8;
        Assert.True(grids![0].TryShiftForward(Longitude, Latitude, out var forwardLatitude, out var forwardLongitude));
        Assert.True(grids![0].TryShiftInverse(forwardLongitude, forwardLatitude, out var backLatitude, out var backLongitude));

        Assert.Equal(Latitude, backLatitude, 9);
        Assert.Equal(Longitude, backLongitude, 9);
    }

    [Fact]
    public void A_point_outside_the_block_is_reported_as_outside_rather_than_extrapolated()
    {
        var grid = Ntv2Fixture.Constant("OSTN15", 49.75, 50.75, -9.0, 0.0, 0.25, 0.25, Shift(1.0, 2.0));
        Assert.True(Ntv2GridReader.TryRead(Ntv2Fixture.ToBytes(grid), "grid.gsb", out var grids, out _));

        Assert.False(grids![0].Covers(-0.1276, 51.5072), "London is north of the block this fixture describes.");
        Assert.False(grids![0].TryShiftForward(-0.1276, 51.5072, out _, out _));
        Assert.False(grids![0].TryShiftInverse(0.5, 50.0, out _, out _));
        Assert.True(grids![0].Covers(-0.1276, 50.0));
    }

    [Fact]
    public void A_bundle_of_several_sub_grids_reads_every_one_of_them()
    {
        var first = Ntv2Fixture.Constant("OSTN15_N", 50.0, 52.0, -8.0, -4.0, 1.0, 1.0, Shift(1.0, 1.0));
        var second = Ntv2Fixture.Constant("OSTN15_S", 49.0, 50.0, -8.0, -4.0, 1.0, 1.0, Shift(2.0, 2.0));

        Assert.True(Ntv2GridReader.TryRead(Ntv2Fixture.ToBytes([first, second]), "bundle.gsb", out var grids, out var error));
        Assert.Null(error);
        Assert.Equal(["OSTN15_N", "OSTN15_S"], grids!.Select(grid => grid.Name));

        // Each sub-grid shifts over its own block, so a point picks the grid
        // that actually covers it rather than the first one in the file.
        Assert.True(grids[0].TryShiftForward(-6.0, 51.0, out var northLatitude, out _));
        Assert.Equal(51.0 + 1.0 * ArcSecond, northLatitude, 12);
        Assert.True(grids[1].TryShiftForward(-6.0, 49.5, out var southLatitude, out _));
        Assert.Equal(49.5 + 2.0 * ArcSecond, southLatitude, 12);
        Assert.False(grids[1].Covers(-6.0, 51.0));
    }

    [Fact]
    public void The_accuracy_stated_is_the_worst_node_in_the_block()
    {
        var nodes = new Ntv2Fixture.Shift[2, 2];
        nodes[0, 0] = new(0f, 0f, 0.05f, 0.05f);
        nodes[0, 1] = new(0f, 0f, 0.10f, 0.05f);
        nodes[1, 0] = new(0f, 0f, 0.05f, 0.20f);
        nodes[1, 1] = new(0f, 0f, 0.05f, 0.05f);

        var grid = new Ntv2Fixture.Grid("N", "A", "B", 50.0, 51.0, 0.0, 1.0, 1.0, 1.0, nodes);
        Assert.True(Ntv2GridReader.TryRead(Ntv2Fixture.ToBytes(grid), "grid.gsb", out var grids, out _));

        // A grid is only as good as its worst node, so that is the number the
        // operation's accuracy is derived from.
        Assert.Equal(0.20, grids![0].AccuracyMetres, 6);
    }

    [Fact]
    public void A_file_that_stops_before_its_data_is_refused_rather_than_half_read()
    {
        var grid = Ntv2Fixture.Constant("OSTN15", 49.75, 50.75, -9.0, 0.0, 0.25, 0.25, Shift(1.0, 2.0));
        var headerBytes = (Ntv2Fixture.OverviewRecordCount + Ntv2Fixture.SubGridRecordCount) * 72;
        var truncated = Ntv2Fixture.TruncatedAfterHeader(grid, headerBytes + 8);

        Assert.False(Ntv2GridReader.TryRead(truncated, "grid.gsb", out var grids, out var error));
        Assert.Empty(grids);
        Assert.NotNull(error);
        Assert.Contains("OSTN15", error, StringComparison.Ordinal);
        Assert.Contains("185", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_grid_is_refused_with_a_reason()
    {
        Assert.False(Ntv2GridReader.TryRead("this is not a grid"u8.ToArray(), "notes.txt", out var grids, out var error));
        Assert.Empty(grids);
        Assert.NotNull(error);
    }

    [Fact]
    public void A_file_named_but_not_present_is_refused_with_a_reason()
    {
        Assert.False(Ntv2GridReader.TryRead("/does/not/exist.gsb", out var grids, out var error));
        Assert.Empty(grids);
        Assert.NotNull(error);
    }
}
