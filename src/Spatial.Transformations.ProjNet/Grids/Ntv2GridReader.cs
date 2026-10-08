using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Spatial.Transformations.ProjNet.Grids;

/// <summary>
/// Reads the NTv2 <c>.gsb</c> bundle format (ADR-0105).
/// <para>
/// A bundle is a header block, then for each sub-grid a header of its own and
/// the shifts for that sub-grid's nodes, terminated by an <c>END</c> record
/// where a header would otherwise start. Every header record is a
/// sixteen-character key and a fifty-six-character value, and the sub-grid
/// header reserves more records than it names — so the keys are looked up by
/// name rather than counted into position, which is what makes a file written
/// by another implementation read the same way.
/// </para>
/// <para>
/// The reader refuses anything it cannot fully account for. A bundle whose
/// node block is short, whose increments do not divide its block, or whose
/// header is not a header at all yields a reason and no grid: a datum shift is
/// exactly the kind of thing that must not be half-read, because a grid that
/// silently covers less than its header claims puts a datum error into every
/// coordinate that crosses the gap.
/// </para>
/// </summary>
internal static class Ntv2GridReader
{
    private const int KeyLength = 16;
    private const int ValueLength = 56;
    private const int RecordLength = KeyLength + ValueLength;
    private const int BytesPerNode = 16;

    /// <summary>How many records the overview header holds, as the format defines it.</summary>
    private const int OverviewRecordCount = 11;

    private const string EndKey = "END";

    private static readonly string[] SubGridKeys =
    [
        "SUB_NAME", "PARENT", "CREATED", "UPDATED", "S_LAT", "N_LAT",
        "E_LONG", "W_LONG", "LAT_INC", "LONG_INC", "GS_COUNT",
    ];

    /// <summary>
    /// Reads a bundle already in memory. Returns every sub-grid in file order;
    /// an empty list with a reason when the bundle cannot be read at all.
    /// </summary>
    public static bool TryRead(byte[] bytes, string fileName, out IReadOnlyList<DatumShiftGrid> grids, out string? error)
    {
        grids = [];
        if (!TryReadOverview(bytes, out var records, out var subGridHeaderSize, out error))
        {
            return false;
        }

        var read = new List<DatumShiftGrid>();
        var offset = records;
        while (offset < bytes.Length)
        {
            // A record where a sub-grid header would start may instead be the
            // bundle's terminator, so the record is read to find out which and
            // rewound if it is a header after all.
            var recordStart = offset;
            if (!TryReadRecord(bytes, ref offset, out var key, out _))
            {
                error = $"'{fileName}' ends inside a header record.";
                return false;
            }

            if (IsKey(key, EndKey))
            {
                error = null;
                grids = read;
                return true;
            }

            offset = recordStart;
            if (!TryReadSubGrid(bytes, ref offset, subGridHeaderSize, fileName, out var grid, out error))
            {
                return false;
            }

            read.Add(grid);
        }

        error = $"'{fileName}' ends after its last sub-grid without the END record that closes the bundle.";
        return false;
    }

    /// <summary>Reads a bundle from a file on disk.</summary>
    public static bool TryRead(string path, out IReadOnlyList<DatumShiftGrid> grids, out string? error)
    {
        if (!File.Exists(path))
        {
            grids = [];
            error = $"The grid file '{Path.GetFileName(path)}' is not in the configured grid directory.";
            return false;
        }

        try
        {
            return TryRead(File.ReadAllBytes(path), Path.GetFileName(path), out grids, out error);
        }
        catch (IOException exception)
        {
            grids = [];
            error = $"The grid file '{Path.GetFileName(path)}' could not be read: {exception.Message}";
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            grids = [];
            error = $"The grid file '{Path.GetFileName(path)}' could not be read: {exception.Message}";
            return false;
        }
    }

    private static bool TryReadOverview(byte[] bytes, out int offset, out int subGridHeaderSize, out string? error)
    {
        offset = 0;
        subGridHeaderSize = 0;
        var overview = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!ReadOverviewRecords(bytes, ref offset, overview, out error)
            || !RequireOverviewCounts(overview, bytes.Length, offset, out subGridHeaderSize, out error))
        {
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// The overview's own records: the first keys the header itself and the
    /// rest are looked up by name rather than counted into position.
    /// </summary>
    private static bool ReadOverviewRecords(byte[] bytes, ref int offset, Dictionary<string, string> overview, out string? error)
    {
        if (!TryReadRecord(bytes, ref offset, out var firstKey, out var firstValue))
        {
            error = "The file is shorter than one NTv2 header record.";
            return false;
        }

        if (!IsKey(firstKey, "NUM_OREC"))
        {
            error = "The file does not begin with an NTv2 overview header (NUM_OREC).";
            return false;
        }

        // Both header sizes are stated by the overview rather than assumed: a
        // reader that counted records itself would only agree with the writer
        // that happened to agree with it.
        overview["NUM_OREC"] = firstValue;
        for (var index = 1; index < OverviewRecordCount; index++)
        {
            if (!TryReadRecord(bytes, ref offset, out var key, out var value))
            {
                error = "The NTv2 overview header stops before its last record.";
                return false;
            }

            overview[key] = value;
        }

        error = null;
        return true;
    }

    /// <summary>Whether the overview's counts describe sub-grids the file can hold.</summary>
    private static bool RequireOverviewCounts(
        Dictionary<string, string> overview, int fileLength, int offset, out int subGridHeaderSize, out string? error)
    {
        subGridHeaderSize = 0;
        if (!TryCount(overview, "NUM_OREC", out var overviewRecords)
            || overviewRecords < OverviewRecordCount)
        {
            error = $"The NTv2 overview header declares {OverviewRecordCount} records but does not hold them.";
            return false;
        }

        if (!TryCount(overview, "NUM_SREC", out var subGridRecords)
            || !TryCount(overview, "NUM_FILE", out var subGridCount)
            || subGridRecords <= 0
            || subGridCount <= 0)
        {
            error = "The NTv2 overview header's record counts are missing or nonsensical.";
            return false;
        }

        subGridHeaderSize = subGridRecords * RecordLength;
        if (fileLength < offset + (subGridCount * BytesPerNode))
        {
            error = $"The NTv2 bundle declares {subGridCount} sub-grid(s) but the file is too short to hold them.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadSubGrid(
        byte[] bytes,
        ref int offset,
        int subGridHeaderSize,
        string fileName,
        out DatumShiftGrid grid,
        out string? error)
    {
        grid = null!;
        var header = new Dictionary<string, string>(StringComparer.Ordinal);
        var headerEnd = offset + subGridHeaderSize;
        if (!CollectSubGridHeader(bytes, ref offset, headerEnd, header, out error))
        {
            return false;
        }

        if (!TryGetName(header, out var name))
        {
            error = $"A sub-grid header in '{fileName}' does not name itself (SUB_NAME).";
            return false;
        }

        if (!TryExtents(header, name, out var extents, out error))
        {
            return false;
        }

        // A bundle may store its block from the north down or from the west
        // back, and the extents are normalised to south-first, west-first so
        // the rest of the reader has one order to think in. The walk direction
        // is remembered before normalising, because the node block has to be
        // reversed by exactly the amount the edges were.
        var walk = new NodeWalk(extents.North < extents.South, extents.East < extents.West);
        extents = extents.Normalised();

        if (!RequireNodeBlock(header, name, extents, out var block, out error))
        {
            return false;
        }

        if (!CheckDataLength(bytes, offset, block.Count, name, out error))
        {
            return false;
        }

        var nodes = ReadNodes(bytes, ref offset, block, walk, name, out error);
        if (nodes is null)
        {
            return false;
        }

        var (latitudeShifts, longitudeShifts, accuracy) = nodes.Value;
        grid = new DatumShiftGrid(
            name,
            Path.GetFileName(fileName),
            GridFormat.Ntv2,
            extents.South, extents.North, extents.West, extents.East,
            extents.LatitudeIncrement, extents.LongitudeIncrement,
            accuracy,
            latitudeShifts,
            longitudeShifts);
        error = null;
        return true;
    }

    /// <summary>The block a sub-grid header states, before normalising.</summary>
    private readonly record struct SubGridExtents(
        double South,
        double North,
        double West,
        double East,
        double LatitudeIncrement,
        double LongitudeIncrement)
    {
        /// <summary>The same block south-first and west-first, with magnitudes for increments.</summary>
        public SubGridExtents Normalised() => new(
            Math.Min(South, North),
            Math.Max(South, North),
            Math.Min(West, East),
            Math.Max(West, East),
            Math.Abs(LatitudeIncrement),
            Math.Abs(LongitudeIncrement));
    }

    /// <summary>The shift block the extents describe: how many shifts, in how many rows and columns.</summary>
    private readonly record struct NodeBlock(int Count, int Rows, int Columns);

    /// <summary>Which way the file walks its node block: rows north-to-south, columns east-to-west, or neither.</summary>
    private readonly record struct NodeWalk(bool NorthToSouth, bool EastToWest);

    /// <summary>
    /// The sub-grid header's known records, looked up by name: the header
    /// reserves more records than it names, so counting into position would
    /// only agree with the writer that happened to agree with it.
    /// </summary>
    private static bool CollectSubGridHeader(
        byte[] bytes, ref int offset, int headerEnd, Dictionary<string, string> header, out string? error)
    {
        while (offset < headerEnd)
        {
            if (!TryReadRecord(bytes, ref offset, out var key, out var value))
            {
                error = "A sub-grid header stops before its last record.";
                return false;
            }

            if (SubGridKeys.Contains(key, StringComparer.Ordinal))
            {
                header[key] = value;
            }
        }

        error = null;
        return true;
    }

    /// <summary>Whether the declared shift count matches the block the extents describe.</summary>
    private static bool RequireNodeBlock(
        Dictionary<string, string> header, string name, SubGridExtents extents, out NodeBlock block, out string? error)
    {
        block = default;
        if (!TryCount(header, "GS_COUNT", out var nodeCount))
        {
            error = $"The sub-grid '{name}' does not state how many shifts it holds (GS_COUNT).";
            return false;
        }

        var (rows, columns) = NodeShape(
            extents.South, extents.North, extents.West, extents.East, extents.LatitudeIncrement, extents.LongitudeIncrement);
        if (rows * columns != nodeCount)
        {
            error = $"The sub-grid '{name}' declares {nodeCount} shifts but its block and increments describe {rows * columns}.";
            return false;
        }

        block = new NodeBlock(nodeCount, rows, columns);
        error = null;
        return true;
    }

    /// <summary>Whether the file holds the shift block the header declares.</summary>
    private static bool CheckDataLength(byte[] bytes, int offset, int nodeCount, string name, out string? error)
    {
        if (offset + (nodeCount * BytesPerNode) > bytes.Length)
        {
            error = $"The sub-grid '{name}' declares {nodeCount} shifts but the file holds fewer than that.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Reads the node block, turning it into a south-first, west-first pair of
    /// shift arrays whatever order the file walked its rows and columns in —
    /// a bundle whose <c>N_LAT</c> sits below its <c>S_LAT</c> is stored
    /// north-to-south, and reading it in file order would shift the block the
    /// wrong way up.
    /// </summary>
    private static (double[] Latitude, double[] Longitude, double Accuracy)? ReadNodes(
        byte[] bytes,
        ref int offset,
        NodeBlock block,
        NodeWalk walk,
        string name,
        out string? error)
    {
        var rows = block.Rows;
        var columns = block.Columns;
        var latitude = new double[rows * columns];
        var longitude = new double[rows * columns];
        var accuracy = 0.0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                if (offset + BytesPerNode > bytes.Length)
                {
                    error = $"The sub-grid '{name}' stops inside its shift block.";
                    return null;
                }

                var latitudeSeconds = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset, 4));
                var longitudeSeconds = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 4, 4));
                accuracy = Math.Max(accuracy, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 8, 4)));
                accuracy = Math.Max(accuracy, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 12, 4)));
                offset += BytesPerNode;

                var target = Index(walk.NorthToSouth ? rows - 1 - row : row, walk.EastToWest ? columns - 1 - column : column, columns);
                latitude[target] = latitudeSeconds;
                longitude[target] = longitudeSeconds;
            }
        }

        error = null;
        return (latitude, longitude, accuracy);
    }

    private static bool TryExtents(
        Dictionary<string, string> header, string name, out SubGridExtents extents, out string? error)
    {
        extents = default;
        if (!TryLatitudes(header, name, out var south, out var north, out var latitudeIncrement, out error)
            || !TryLongitudes(header, name, out var west, out var east, out var longitudeIncrement, out error))
        {
            return false;
        }

        if (latitudeIncrement == 0.0 || longitudeIncrement == 0.0)
        {
            error = $"The sub-grid '{name}' declares a zero increment, so its block has no nodes.";
            return false;
        }

        extents = new SubGridExtents(south, north, west, east, latitudeIncrement, longitudeIncrement);
        error = null;
        return true;
    }

    /// <summary>The latitudinal half of the block: its south and north edges and the increment between them.</summary>
    private static bool TryLatitudes(
        Dictionary<string, string> header, string name, out double south, out double north, out double increment, out string? error)
    {
        south = north = increment = 0.0;
        if (!TryNumber(header, name, "S_LAT", out south)
            || !TryNumber(header, name, "N_LAT", out north)
            || !TryNumber(header, name, "LAT_INC", out increment))
        {
            error = $"The sub-grid '{name}' does not state a complete, readable block.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>The longitudinal half of the block: its west and east edges and the increment between them.</summary>
    private static bool TryLongitudes(
        Dictionary<string, string> header, string name, out double west, out double east, out double increment, out string? error)
    {
        west = east = increment = 0.0;
        if (!TryNumber(header, name, "W_LONG", out west)
            || !TryNumber(header, name, "E_LONG", out east)
            || !TryNumber(header, name, "LONG_INC", out increment))
        {
            error = $"The sub-grid '{name}' does not state a complete, readable block.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// How many nodes a block holds, from its edges and its increments. The
    /// increments are taken as magnitudes: the direction of the walk is the
    /// caller's business, and the reader normalises afterwards.
    /// </summary>
    private static (int Rows, int Columns) NodeShape(
        double south,
        double north,
        double west,
        double east,
        double latitudeIncrement,
        double longitudeIncrement)
    {
        var rows = (int)Math.Round(Math.Abs(north - south) / Math.Abs(latitudeIncrement)) + 1;
        var columns = (int)Math.Round(Math.Abs(east - west) / Math.Abs(longitudeIncrement)) + 1;
        return (rows, columns);
    }

    private static int Index(int row, int column, int columns) => (row * columns) + column;

    private static bool TryGetName(Dictionary<string, string> header, out string name)
    {
        if (header.TryGetValue("SUB_NAME", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            name = raw.Trim();
            return true;
        }

        name = string.Empty;
        return false;
    }

    private static bool TryCount(Dictionary<string, string> header, string key, out int value)
    {
        value = 0;
        return header.TryGetValue(key, out var raw)
            && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryNumber(Dictionary<string, string> header, string name, string key, out double value)
    {
        value = 0.0;
        return header.TryGetValue(key, out var raw)
            && double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
    /// <summary>
    /// Reads one sixteen-plus-fifty-six byte header record. Values are padded
    /// with spaces in most bundles and with NUL bytes in some, so both are
    /// stripped; the key is positional within the record, so it is only
    /// trimmed of the same padding.
    /// </summary>
    private static bool TryReadRecord(byte[] bytes, ref int offset, out string key, out string value)
    {
        key = value = string.Empty;
        if (offset + RecordLength > bytes.Length)
        {
            return false;
        }

        key = Clean(Encoding.ASCII.GetString(bytes, offset, KeyLength));
        value = Clean(Encoding.ASCII.GetString(bytes, offset + KeyLength, ValueLength));
        offset += RecordLength;
        return true;
    }

    /// <summary>Strips the padding a header record carries, whether it is spaces or NUL bytes.</summary>
    private static string Clean(string field) => field.Trim(' ', '\0', '\t', '\r', '\n');

    private static bool IsKey(string key, string expected) => string.Equals(key, expected, StringComparison.Ordinal);
}
