using System.Globalization;
using System.Text;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Builds a real NADCON <c>.las</c>/<c>.los</c> byte pair, in the layout
/// <c>NadconGridReader</c> reads on disk.
/// <para>
/// The bytes are written here rather than vendored for the reason
/// <see cref="Ntv2Fixture"/> states: every published NADCON bundle is
/// third-party data with its own licence, and ADR-0105 §licence keeps it out
/// of the engine and out of its tests. Writing the bytes pins the format the
/// reader parses, and the shifts the fixture chooses make the expected answer
/// arithmetic rather than a remembered number.
/// </para>
/// <para>
/// NADCON's own container is a pair rather than a bundle — the <c>.las</c>
/// holds the latitude shifts and the <c>.los</c> the longitude shifts — so
/// this fixture produces a pair, and <see cref="ToBytes(Grid, bool)"/> exists
/// for the test that damages one half of it.
/// </para>
/// <para>
/// The bytes are written in the container's convention, which is not the one
/// the datums are tabulated in: <see cref="Grid.Shifts"/> carries what the
/// <em>file</em> states, so a <c>.los</c> value is positive west, and the
/// header's longitudes are positive west too. That is not this fixture's
/// invention — it is what a deployed pair holds (ADR-0181), and a fixture that
/// wrote the datums' own convention would pin the reader against a file NADCON
/// does not publish. The expected <em>answers</em> are therefore stated in
/// degrees east-positive, which is where the conversion belongs.
/// </para>
/// </summary>
internal static class NadconFixture
{
    /// <summary>Every NADCON header record is 64 characters wide.</summary>
    public const int RecordLength = 64;

    /// <summary>The overview header carries three records: the two sizes and the sub-grid count.</summary>
    public const int OverviewRecordCount = 3;

    /// <summary>
    /// The sub-grid header the fixture writes: its identity record and then
    /// its block. A NADCON header is a run of 64-byte records, so a block's
    /// numbers run across as many of them as they need — which is why the
    /// reader reads them as numbers rather than into fixed columns.
    /// </summary>
    public const int SubGridRecordCount = 3;

    /// <summary>How many numbers a sub-grid header states its block in.</summary>
    public const int BlockNumberCount = 10;

    /// <summary>
    /// A grid description: the block it covers, the increments the nodes sit
    /// at, and the shift at every node as the <em>file</em> states it —
    /// positive north for the <c>.las</c> half and positive west for the
    /// <c>.los</c> half — supplied row-major from the south-west corner. The
    /// block itself is given in degrees east-positive, as the datums are; the
    /// fixture negates its longitudes on the way into the header.
    /// <para>
    /// The nodes are written in NADCON's own order — north row first — which
    /// is what makes the reader's normalisation a thing the tests exercise.
    /// </para>
    /// </summary>
    public sealed record Grid(
        string SubGridName,
        double SouthLatitude,
        double NorthLatitude,
        double WestLongitude,
        double EastLongitude,
        double LatitudeIncrement,
        double LongitudeIncrement,
        float[,] Shifts);

    public static int Rows(Grid grid) => grid.Shifts.GetLength(0) - 1;

    public static int Columns(Grid grid) => grid.Shifts.GetLength(1) - 1;

    private static int Rows(double south, double north, double increment) =>
        (int)Math.Round((north - south) / increment);

    private static int Columns(double west, double east, double increment) =>
        (int)Math.Round((east - west) / increment);

    /// <summary>
    /// A grid whose shift is a constant: every node carries the same value, so
    /// the interpolated shift is that value everywhere inside the block.
    /// </summary>
    public static Grid Constant(
        string name,
        double south,
        double north,
        double west,
        double east,
        double latitudeIncrement,
        double longitudeIncrement,
        float seconds)
    {
        var nodes = new float[Rows(south, north, latitudeIncrement) + 1, Columns(west, east, longitudeIncrement) + 1];
        for (var row = 0; row < nodes.GetLength(0); row++)
        {
            for (var column = 0; column < nodes.GetLength(1); column++)
            {
                nodes[row, column] = seconds;
            }
        }

        return new Grid(name, south, north, west, east, latitudeIncrement, longitudeIncrement, nodes);
    }

    /// <summary>The matching pair: the latitude file and the longitude file.</summary>
    public static (byte[] Latitude, byte[] Longitude) ToPair(Grid grid) =>
        (ToBytes(grid, longitude: false), ToBytes(grid, longitude: true));

    /// <summary>
    /// Serialises one half of the pair: the three-record overview, the
    /// sub-grid header, the shift block, and the <c>END</c> record. NADCON
    /// walks its rows from the north down and its columns from the west up,
    /// so the bytes are written in that order whatever order the caller
    /// supplied the nodes in.
    /// </summary>
    public static byte[] ToBytes(Grid grid, bool longitude)
    {
        var stream = new MemoryStream();
        WriteRecord(stream, "NUMOREC", OverviewRecordCount.ToString(CultureInfo.InvariantCulture));
        WriteRecord(stream, "NUMSREC", SubGridRecordCount.ToString(CultureInfo.InvariantCulture));
        WriteRecord(stream, "NUMFILE", "1");

        WriteRecord(stream, "ID", grid.SubGridName);
        WriteBlock(stream, Block(grid));

        for (var row = Rows(grid); row >= 0; row--)
        {
            for (var column = 0; column <= Columns(grid); column++)
            {
                var node = BitConverter.GetBytes(Stated(grid, row, column, longitude));
                if (BitConverter.IsLittleEndian)
                {
                    stream.Write(node);
                }
                else
                {
                    stream.Write([.. node.Reverse()]);
                }
            }
        }

        WriteRecord(stream, "END", string.Empty);
        return stream.ToArray();
    }

    /// <summary>
    /// The ten numbers the header states, in the order the reader reads them:
    /// the four edges, the two increments, the two node counts, and the
    /// south-west corner the increments are measured from. The longitudes are
    /// written positive west, in both halves and identically — a deployed
    /// pair's two headers are the same header — so a reader that took them as
    /// written would place the block on the wrong side of the prime meridian.
    /// </summary>
    public static string Block(Grid grid) => string.Join(
        " ",
        Number(grid.SouthLatitude),
        Number(grid.NorthLatitude),
        Number(-grid.EastLongitude),
        Number(-grid.WestLongitude),
        Number(grid.LatitudeIncrement),
        Number(grid.LongitudeIncrement),
        (Rows(grid) + 1).ToString(CultureInfo.InvariantCulture),
        (Columns(grid) + 1).ToString(CultureInfo.InvariantCulture),
        Number(grid.SouthLatitude),
        Number(-grid.WestLongitude));

    /// <summary>
    /// What the node holds as the file states it: the <c>.las</c> half carries
    /// latitude shifts positive north and the <c>.los</c> half longitude
    /// shifts positive west, so the longitude half is negated on the way out.
    /// </summary>
    private static float Stated(Grid grid, int row, int column, bool longitude) =>
        longitude ? -grid.Shifts[row, column] : grid.Shifts[row, column];

    private static string Number(double value) => value.ToString("F8", CultureInfo.InvariantCulture);

    /// <summary>
    /// Writes a sub-grid's block across the header records that follow its
    /// identity, filling each record's value area and starting a new one
    /// rather than running off the end of it — the layout NADCON itself uses.
    /// </summary>
    private static void WriteBlock(Stream stream, string block)
    {
        var valueLength = RecordLength - 8;
        var start = 0;
        while (start < block.Length)
        {
            var written = Math.Min(valueLength, block.Length - start);

            // Never split a number across two records: a header whose block
            // ran off the end of a record wraps to the next one whole, and the
            // reader would otherwise read two halves as two numbers.
            if (start + written < block.Length)
            {
                var boundary = block.LastIndexOf(' ', start + written - 1, written);
                written = boundary > start ? boundary - start + 1 : written;
            }

            WriteRecord(stream, string.Empty, block.Substring(start, written));
            start += written;
        }
    }

    private static void WriteRecord(Stream stream, string key, string value)
    {
        var record = new byte[RecordLength];
        Encoding.ASCII.GetBytes(key, 0, Math.Min(key.Length, 8), record, 0);
        Encoding.ASCII.GetBytes(value, 0, Math.Min(value.Length, RecordLength - 8), record, 8);
        stream.Write(record);
    }
}