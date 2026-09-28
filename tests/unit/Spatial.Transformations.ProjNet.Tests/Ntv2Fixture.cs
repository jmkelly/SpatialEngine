using System.Buffers.Binary;
using System.Text;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Builds a real NTv2 <c>.gsb</c> byte stream, in the format the reader has to
/// cope with on disk.
/// <para>
/// The fixture is written here rather than vendored because every published
/// bundle — OSTN15, NADCON, the Australian and Canadian grids — is third-party
/// data with its own licence and redistribution terms (ADR-0105 §licence), and
/// the engine's tests must not depend on fetching one. Writing the bytes means
/// the format is pinned by the test itself: a header laid out as the
/// specification describes, and shifts this test chose so the expected
/// interpolation is arithmetic rather than a remembered number.
/// </para>
/// </summary>
internal static class Ntv2Fixture
{
    /// <summary>Every NTv2 header record is a 16-character key and a 56-character value.</summary>
    private const int KeyLength = 16;

    private const int ValueLength = 56;
    private const int RecordLength = KeyLength + ValueLength;

    /// <summary>The overview header carries exactly eleven records.</summary>
    public const int OverviewRecordCount = 11;

    /// <summary>The sub-grid header carries eleven, named by NUM_SREC.</summary>
    public const int SubGridRecordCount = 11;

    /// <summary>A latitude/longitude shift in arc-seconds, with the accuracy EPSG would register.</summary>
    public readonly record struct Shift(float LatitudeSeconds, float LongitudeSeconds, float LatitudeAccuracyMetres, float LongitudeAccuracyMetres);

    /// <summary>
    /// A grid description: the block it covers, its increments, and the shift
    /// at every node, supplied row-major from the south-west corner.
    /// </summary>
    public sealed record Grid(
        string SubGridName,
        string SystemFrom,
        string SystemTo,
        double SouthLatitude,
        double NorthLatitude,
        double WestLongitude,
        double EastLongitude,
        double LatitudeIncrement,
        double LongitudeIncrement,
        Shift[,] Nodes);

    /// <summary>The number of rows and columns the grid's own increments imply.</summary>
    public static int Columns(Grid grid) => grid.Nodes.GetLength(1) - 1;

    public static int Rows(Grid grid) => grid.Nodes.GetLength(0) - 1;

    /// <summary>
    /// A grid whose shift is a constant: every node carries the same value, so
    /// the interpolated shift is that value everywhere inside the block and the
    /// expected result is the same arithmetic the test writes down.
    /// </summary>
    public static Grid Constant(
        string name,
        double south,
        double north,
        double west,
        double east,
        double latitudeIncrement,
        double longitudeIncrement,
        Shift shift)
    {
        var columns = (int)Math.Round((east - west) / longitudeIncrement);
        var rows = (int)Math.Round((north - south) / latitudeIncrement);
        var nodes = new Shift[rows + 1, columns + 1];
        for (var row = 0; row <= rows; row++)
        {
            for (var column = 0; column <= columns; column++)
            {
                nodes[row, column] = shift;
            }
        }

        return new Grid(name, "OSGB36", "WGS84", south, north, west, east, latitudeIncrement, longitudeIncrement, nodes);
    }

    /// <summary>
    /// Serialises a grid into the <c>.gsb</c> byte layout: the eleven-record
    /// overview, then for each sub-grid a nineteen-record header and the
    /// sixteen-byte-per-node shift block, terminated by the <c>END</c> record.
    /// </summary>
    public static byte[] ToBytes(Grid grid)
    {
        var stream = new MemoryStream();
        WriteOverview(stream, grid, 1);
        WriteSubGrid(stream, grid);
        WriteRecord(stream, "END", string.Empty);
        return stream.ToArray();
    }

    /// <summary>Two sub-grids in one bundle, the overview's sub-grid count agreeing.</summary>
    public static byte[] ToBytes(Grid[] grids)
    {
        var stream = new MemoryStream();
        WriteOverview(stream, grids[0], grids.Length);
        foreach (var grid in grids)
        {
            WriteSubGrid(stream, grid);
        }

        WriteRecord(stream, "END", string.Empty);
        return stream.ToArray();
    }

    /// <summary>A bundle whose overview block is written but whose data is truncated.</summary>
    public static byte[] TruncatedAfterHeader(Grid grid, int bytesToKeep)
    {
        var full = ToBytes(grid);
        return full[..Math.Min(bytesToKeep, full.Length)];
    }

    private static void WriteOverview(Stream stream, Grid grid, int subGridCount)
    {
        WriteRecord(stream, "NUM_OREC", "11");
        WriteRecord(stream, "NUM_SREC", "11");
        WriteRecord(stream, "NUM_FILE", subGridCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteRecord(stream, "GS_TYPE", "SECONDS");
        WriteRecord(stream, "VERSION", "NTv2.0");
        WriteRecord(stream, "SYSTEM_F", grid.SystemFrom);
        WriteRecord(stream, "SYSTEM_T", grid.SystemTo);
        WriteRecord(stream, "MAJOR_F", "6360");
        WriteRecord(stream, "MINOR_F", "0");
        WriteRecord(stream, "MAJOR_T", "6360");
        WriteRecord(stream, "MINOR_T", "0");
    }

    private static void WriteSubGrid(Stream stream, Grid grid)
    {
        WriteRecord(stream, "SUB_NAME", grid.SubGridName);
        WriteRecord(stream, "PARENT", "NONE");
        WriteRecord(stream, "CREATED", "01012000");
        WriteRecord(stream, "UPDATED", "01012000");
        WriteRecord(stream, "S_LAT", grid.SouthLatitude.ToString("F12", System.Globalization.CultureInfo.InvariantCulture));
        WriteRecord(stream, "N_LAT", grid.NorthLatitude.ToString("F12", System.Globalization.CultureInfo.InvariantCulture));
        WriteRecord(stream, "E_LONG", grid.EastLongitude.ToString("F12", System.Globalization.CultureInfo.InvariantCulture));
        WriteRecord(stream, "W_LONG", grid.WestLongitude.ToString("F12", System.Globalization.CultureInfo.InvariantCulture));
        WriteRecord(stream, "LAT_INC", grid.LatitudeIncrement.ToString("F12", System.Globalization.CultureInfo.InvariantCulture));
        WriteRecord(stream, "LONG_INC", grid.LongitudeIncrement.ToString("F12", System.Globalization.CultureInfo.InvariantCulture));
        WriteRecord(stream, "GS_COUNT", (grid.Nodes.GetLength(0) * grid.Nodes.GetLength(1))
            .ToString(System.Globalization.CultureInfo.InvariantCulture));

        Span<byte> record = stackalloc byte[16];
        for (var row = 0; row < grid.Nodes.GetLength(0); row++)
        {
            for (var column = 0; column < grid.Nodes.GetLength(1); column++)
            {
                var node = grid.Nodes[row, column];
                BinaryPrimitives.WriteSingleLittleEndian(record[..4], node.LatitudeSeconds);
                BinaryPrimitives.WriteSingleLittleEndian(record.Slice(4, 4), node.LongitudeSeconds);
                BinaryPrimitives.WriteSingleLittleEndian(record.Slice(8, 4), node.LatitudeAccuracyMetres);
                BinaryPrimitives.WriteSingleLittleEndian(record.Slice(12, 4), node.LongitudeAccuracyMetres);
                stream.Write(record);
            }
        }
    }

    private static void WriteRecord(Stream stream, string key, string value)
    {
        var record = new byte[RecordLength];
        Encoding.ASCII.GetBytes(key, 0, Math.Min(key.Length, KeyLength), record, 0);
        Encoding.ASCII.GetBytes(value, 0, Math.Min(value.Length, ValueLength), record, KeyLength);
        stream.Write(record);
    }
}
