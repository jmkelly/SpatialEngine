using System.Text;
using Spatial.Transformations.ProjNet.Grids;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The NTv2 reader's refusals: every bundle the reader cannot fully account
/// for yields a reason and no grid, because a datum shift must not be
/// half-read. Each test damages one statement of a fixture-written bundle and
/// pins the refusal it produces.
/// </summary>
public sealed class Ntv2GridReaderValidationTests
{
    private const int KeyLength = 16;

    private const int ValueLength = 56;

    private const int RecordLength = KeyLength + ValueLength;

    private static Ntv2Fixture.Shift Shift(double latitudeSeconds, double longitudeSeconds) =>
        new((float)latitudeSeconds, (float)longitudeSeconds, 0.05f, 0.05f);

    private static byte[] Bundle() =>
        Ntv2Fixture.ToBytes(Ntv2Fixture.Constant("OSTN15", 49.75, 50.75, -9.0, 0.0, 0.25, 0.25, Shift(1.0, 2.0)));

    private static byte[] PatchRecord(byte[] bytes, int record, string? key, string? value)
    {
        var patched = (byte[])bytes.Clone();
        if (key is not null)
        {
            Array.Clear(patched, record * RecordLength, KeyLength);
            Encoding.ASCII.GetBytes(key).CopyTo(patched.AsSpan(record * RecordLength, Math.Min(key.Length, KeyLength)));
        }

        if (value is not null)
        {
            Array.Clear(patched, record * RecordLength + KeyLength, ValueLength);
            Encoding.ASCII.GetBytes(value).CopyTo(patched.AsSpan(
                record * RecordLength + KeyLength, Math.Min(value.Length, ValueLength)));
        }

        return patched;
    }

    [Fact]
    public void A_file_shorter_than_one_record_is_refused()
    {
        Assert.False(Ntv2GridReader.TryRead(new byte[10], "grid.gsb", out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("shorter than one NTv2 header record", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_does_not_begin_with_an_overview_is_refused()
    {
        var renamed = PatchRecord(Bundle(), record: 0, key: "NUM_XXXX", value: null);

        Assert.False(Ntv2GridReader.TryRead(renamed, "grid.gsb", out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("NUM_OREC", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overview_that_stops_early_is_refused()
    {
        var truncated = Bundle()[..(5 * RecordLength)];

        Assert.False(Ntv2GridReader.TryRead(truncated, "grid.gsb", out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("stops before its last record", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overview_whose_counts_disagree_with_its_records_is_refused()
    {
        var understated = PatchRecord(Bundle(), record: 0, key: null, value: "5");

        Assert.False(Ntv2GridReader.TryRead(understated, "grid.gsb", out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("does not hold them", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overview_with_nonsensical_record_counts_is_refused()
    {
        var zeroFiles = PatchRecord(Bundle(), record: 2, key: null, value: "0");

        Assert.False(Ntv2GridReader.TryRead(zeroFiles, "grid.gsb", out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("missing or nonsensical", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bundle_declaring_more_sub_grids_than_its_bytes_hold_is_refused()
    {
        var crowded = PatchRecord(Bundle(), record: 2, key: null, value: "100000");

        Assert.False(Ntv2GridReader.TryRead(crowded, "grid.gsb", out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("too short to hold them", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sub_grid_header_that_stops_early_is_refused()
    {
        var truncated = Bundle()[..((Ntv2Fixture.OverviewRecordCount + 3) * RecordLength)];

        Assert.False(Ntv2GridReader.TryRead(truncated, "grid.gsb", out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("stops before its last record", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sub_grid_header_with_no_name_is_refused()
    {
        var nameless = PatchRecord(Bundle(), record: Ntv2Fixture.OverviewRecordCount, key: "NO_NAME", value: null);

        Assert.False(Ntv2GridReader.TryRead(nameless, "grid.gsb", out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("does not name itself", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sub_grid_with_no_shift_count_is_refused()
    {
        // GS_COUNT is the eleventh record of the sub-grid header.
        var countess = PatchRecord(Bundle(), record: Ntv2Fixture.OverviewRecordCount + 10, key: "GS_XXXX", value: null);

        Assert.False(Ntv2GridReader.TryRead(countess, "grid.gsb", out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("OSTN15", error, StringComparison.Ordinal);
        Assert.Contains("GS_COUNT", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sub_grid_whose_shift_count_disagrees_with_its_block_is_refused()
    {
        var miscounted = PatchRecord(Bundle(), record: Ntv2Fixture.OverviewRecordCount + 10, key: null, value: "7");

        Assert.False(Ntv2GridReader.TryRead(miscounted, "grid.gsb", out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("declares 7 shifts", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sub_grid_with_an_unreadable_block_is_refused()
    {
        // S_LAT is the fifth record of the sub-grid header.
        var blind = PatchRecord(Bundle(), record: Ntv2Fixture.OverviewRecordCount + 4, key: "XX_LAT", value: null);

        Assert.False(Ntv2GridReader.TryRead(blind, "grid.gsb", out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("complete, readable block", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sub_grid_with_a_zero_increment_is_refused()
    {
        // LAT_INC is the ninth record of the sub-grid header.
        var flat = PatchRecord(Bundle(), record: Ntv2Fixture.OverviewRecordCount + 8, key: null, value: "0");

        Assert.False(Ntv2GridReader.TryRead(flat, "grid.gsb", out var grids, out var error));
        Assert.Empty(grids);
        Assert.Contains("zero increment", error, StringComparison.Ordinal);
    }
}
