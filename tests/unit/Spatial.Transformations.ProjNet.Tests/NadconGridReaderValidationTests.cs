using System.Text;
using Spatial.Transformations.ProjNet.Grids;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The NADCON reader's refusals: every header the pair cannot account for
/// yields a reason and no grid, because a datum shift must not be half-read.
/// Each test damages one statement of a fixture-written pair and pins the
/// refusal it produces.
/// </summary>
public sealed class NadconGridReaderValidationTests
{
    private const double StatedAccuracy = 0.15;

    private const string LatitudeName = "conus.las";

    private const string LongitudeName = "conus.los";

    private static (byte[] Latitude, byte[] Longitude) Pair() =>
        NadconFixture.ToPair(NadconFixture.Constant("CONUS", 24.0, 26.0, -84.0, -82.0, 1.0, 1.0, 1.0f));

    private static byte[] PatchRecord(byte[] bytes, int record, string? key, string? value)
    {
        var patched = (byte[])bytes.Clone();
        if (key is not null)
        {
            Array.Clear(patched, record * NadconFixture.RecordLength, 8);
            Encoding.ASCII.GetBytes(key).CopyTo(patched.AsSpan(record * NadconFixture.RecordLength, Math.Min(key.Length, 8)));
        }

        if (value is not null)
        {
            Array.Clear(patched, record * NadconFixture.RecordLength + 8, NadconFixture.RecordLength - 8);
            Encoding.ASCII.GetBytes(value).CopyTo(patched.AsSpan(
                record * NadconFixture.RecordLength + 8, Math.Min(value.Length, NadconFixture.RecordLength - 8)));
        }

        return patched;
    }

    /// <summary>
    /// Replaces one of the ten numbers the sub-grid header states, keeping
    /// the records themselves intact: the replacement is written back across
    /// the block records' value areas in order, so keys and alignment are
    /// untouched.
    /// </summary>
    private static byte[] WithBlockNumber(byte[] bytes, int index, string value)
    {
        const int ValueLength = NadconFixture.RecordLength - 8;
        var first = (NadconFixture.OverviewRecordCount + 1) * NadconFixture.RecordLength;
        var records = NadconFixture.SubGridRecordCount - 1;

        var text = string.Empty;
        for (var record = 0; record < records; record++)
        {
            text += Encoding.ASCII.GetString(bytes, first + (record * NadconFixture.RecordLength) + 8, ValueLength);
        }

        var tokens = text.Split([' ', '\0'], StringSplitOptions.RemoveEmptyEntries);
        tokens[index] = value;

        var patched = (byte[])bytes.Clone();
        for (var record = 0; record < records; record++)
        {
            Array.Clear(patched, first + (record * NadconFixture.RecordLength) + 8, ValueLength);
        }

        // Repacked the way the fixture packs: whole numbers per value area,
        // never split across two records, so the reader sees the same tokens
        // it would in a file written that way.
        var area = 0;
        var used = 0;
        foreach (var token in tokens)
        {
            Assert.True(token.Length <= ValueLength, "a block number has to fit one record's value area.");
            if (used > 0 && used + 1 + token.Length > ValueLength)
            {
                area++;
                used = 0;
            }

            Assert.True(area < records, "the test's replacement has to fit the records it is written into.");
            var at = first + (area * NadconFixture.RecordLength) + 8 + used;
            if (used > 0)
            {
                patched[at] = (byte)' ';
                at++;
                used++;
            }

            Encoding.ASCII.GetBytes(token).CopyTo(patched.AsSpan(at, token.Length));
            used += token.Length;
        }

        return patched;
    }

    [Fact]
    public void A_file_shorter_than_one_record_is_refused()
    {
        var empty = new byte[10];

        Assert.False(NadconGridReader.TryRead(empty, LatitudeName, empty, LongitudeName, StatedAccuracy, out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("overview header", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_overview_whose_counts_disagree_with_its_records_is_refused()
    {
        var (latitude, longitude) = Pair();
        var understated = PatchRecord(latitude, record: 0, key: null, value: "2");

        Assert.False(NadconGridReader.TryRead(understated, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("does not hold them", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overview_with_a_nonsensical_sub_grid_count_is_refused()
    {
        var (latitude, longitude) = Pair();
        var zeroGrids = PatchRecord(latitude, record: 2, key: null, value: "0");

        Assert.False(NadconGridReader.TryRead(zeroGrids, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("missing or nonsensical", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sub_grid_header_that_stops_early_is_refused()
    {
        var (latitude, longitude) = Pair();
        // Into the sub-grid header but short of its last record: the
        // overview (three records) and the identity record read, and the
        // next record is not there.
        var truncated = latitude[..(5 * NadconFixture.RecordLength)];

        Assert.False(NadconGridReader.TryRead(truncated, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("stops before its last record", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sub_grid_header_with_no_identity_is_refused()
    {
        var (latitude, longitude) = Pair();
        var nameless = PatchRecord(latitude, record: 3, key: "XX", value: null);

        Assert.False(NadconGridReader.TryRead(nameless, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("does not name itself", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_with_a_zero_increment_is_refused()
    {
        var (latitude, longitude) = Pair();
        var flat = WithBlockNumber(latitude, index: 4, value: "0");

        Assert.False(NadconGridReader.TryRead(flat, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("zero increment", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_node_block_is_refused_for_having_no_cell()
    {
        var single = NadconFixture.ToPair(NadconFixture.Constant("DOT", 24.0, 24.0, -84.0, -84.0, 1.0, 1.0, 1.0f));

        Assert.False(NadconGridReader.TryRead(single.Latitude, LatitudeName, single.Longitude, LongitudeName, StatedAccuracy, out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("no cell to interpolate", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_anchored_away_from_its_south_west_corner_is_refused()
    {
        var (latitude, longitude) = Pair();
        var moved = WithBlockNumber(latitude, index: 8, value: "25");

        Assert.False(NadconGridReader.TryRead(moved, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("not its south-west corner", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_whose_counts_do_not_reach_its_north_edge_is_refused()
    {
        // Three rows at a 0.9-degree increment from 24 reach 25.8, not the
        // stated 26: the counts agree with the edges' shape but the lattice
        // they describe stops short of it.
        var (latitude, longitude) = Pair();
        var short_ = WithBlockNumber(latitude, index: 4, value: "0.9");

        Assert.False(NadconGridReader.TryRead(short_, LatitudeName, longitude, LongitudeName, StatedAccuracy, out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("does not reach its stated north", error, StringComparison.Ordinal);
    }
}
