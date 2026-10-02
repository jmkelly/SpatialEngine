using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Spatial.Transformations.ProjNet.Grids;

/// <summary>
/// Reads the NADCON <c>.las</c>/<c>.los</c> pair (ADR-0105 §1, as amended by
/// ADR-0168), into the same <see cref="DatumShiftGrid"/> the NTv2 reader
/// produces, so nothing downstream — the registry, the graph, the published
/// operation — knows which standard served a datum.
/// <para>
/// NADCON's container is not a bundle. An NTv2 <c>.gsb</c> holds one block per
/// sub-grid with all four of a node's values in it; a NADCON deployment is a
/// <em>pair</em> of files with identical headers, the <c>.las</c> carrying the
/// latitude shifts and the <c>.los</c> the longitude shifts, one
/// single-precision value per node. The two halves are read as one grid and
/// are cross-checked against each other's headers: a pair that does not agree
/// about the block it covers is refused rather than joined, because a latitude
/// shift tabulated over one block and a longitude shift over another is a
/// shift nobody computed.
/// </para>
/// <para>
/// The header is a run of 64-byte records. The overview's first three are
/// keyed by their leading eight characters — <c>NUMOREC</c>,
/// <c>NUMSREC</c> and <c>NUMFILE</c> — and a sub-grid's own header follows,
/// one of whose records is keyed <c>ID</c> and names the block. The block's
/// own numbers are read <em>as numbers</em> rather than into fixed columns,
/// in order, ignoring the text between them: which records a header spends on
/// identity, source and units is a detail of the file, and a reader that
/// counted columns would refuse a header written by another implementation for
/// no better reason than that its padding differed.
/// </para>
/// <para>
/// The numbers, in order: the south, north, east and west edges; the latitude
/// and longitude increments; the row and column counts; and the south-west
/// corner the increments are measured from. The block is therefore stated
/// three times over, and the reader reads all three and requires them to
/// agree — the edges against the increments, the counts against the shape
/// those imply, and the corner against the block they describe. A NADCON
/// grid's shifts are tabulated on a fixed lattice and a header that cannot
/// account for its own lattice would put a datum error into every coordinate
/// that crosses the gap.
/// </para>
/// <para>
/// Two things a NADCON shift record does not carry are handled by not being
/// invented. There is no per-node accuracy, so the grid's accuracy is the
/// figure its catalogue row publishes rather than the worst node read off the
/// file (ADR-0168 §2), and there is no interpolation choice, so the grid is
/// the same bilinear one every other grid here is.
/// </para>
/// <para>
/// The container keeps its sign the other way round from the datums it
/// serves, and this reader is where that is answered. Every longitude a
/// NADCON deployment states — the block's edges, the corner its increments run
/// from, and the shifts the <c>.los</c> half tabulates — is positive west,
/// because the method was built for NAD27's positive-west longitudes. EPSG
/// says so on the operations themselves (1241, 1243 and 15864 each note that
/// the NADCON method "expects longitudes positive west" while the datums the
/// grid reaches are positive east), and PROJ 9.8.1 negates the header extents
/// and the node values for the same reason. Both are negated here, so the
/// <see cref="DatumShiftGrid"/> a pair produces is tabulated in the datum's
/// own convention and nothing downstream — the registry, the graph, the
/// published operation — carries the container's sign. This is the whole of
/// the difference between the two containers ADR-0168 §1 speaks of, alongside
/// the pairing itself.
/// </para>
/// <para>
/// What the byte layout is verified against is stated rather than assumed: it
/// is pinned by this repository's own fixture and by nothing else, because
/// ADR-0105 §licence keeps published bundles out of the engine and its tests.
/// A real NADCON bundle that this reader refuses is refused loudly with the
/// reason, and the offsets are the first thing to check against the
/// publishing agency's documentation (SpatialEngine-yt2).
/// </para>
/// </summary>
internal static class NadconGridReader
{
    /// <summary>Every NADCON header record is 64 characters wide.</summary>
    private const int RecordLength = 64;

    /// <summary>A NADCON shift is one single-precision value in arc-seconds.</summary>
    private const int BytesPerNode = 4;

    /// <summary>How many overview records the reader reads for their three counts.</summary>
    private const int OverviewRecordCount = 3;

    /// <summary>How many numbers a sub-grid header states its block in.</summary>
    private const int BlockNumberCount = 10;

    private const string OverviewKey = "NUMOREC";
    private const string SubGridRecordCountKey = "NUMSREC";
    private const string SubGridCountKey = "NUMFILE";
    private const string IdentityKey = "ID";
    private const string EndKey = "END";

    /// <summary>
    /// How close two statements of the same edge have to be, in degrees. The
    /// header's numbers are written as text, so the cross-checks are against
    /// a tolerance rather than for equality; a tenth of a millimetre is far
    /// below anything a grid's lattice is stated to.
    /// </summary>
    private const double EdgeTolerance = 1e-7;

    /// <summary>
    /// Reads a pair already in memory. Returns every sub-grid in file order,
    /// each joined with its counterpart from the other half; an empty list
    /// with a reason when the pair cannot be read at all.
    /// </summary>
    /// <param name="latitudeBytes">The <c>.las</c> file's bytes.</param>
    /// <param name="longitudeBytes">The <c>.los</c> file's bytes.</param>
    /// <param name="accuracyMetres">
    /// The accuracy to publish for the grid, in metres. A NADCON shift record
    /// holds no accuracy of its own, so this is the figure the catalogue row
    /// carries rather than one read out of the file.
    /// </param>
    public static bool TryRead(
        byte[] latitudeBytes,
        string latitudeFileName,
        byte[] longitudeBytes,
        string longitudeFileName,
        double accuracyMetres,
        out IReadOnlyList<DatumShiftGrid> grids,
        out string? error)
    {
        grids = [];
        var latitudeName = Path.GetFileName(latitudeFileName);
        var longitudeName = Path.GetFileName(longitudeFileName);
        if (!TryReadFile(latitudeBytes, out var latitudes, out error)
            || !TryReadFile(longitudeBytes, out var longitudes, out error))
        {
            return false;
        }

        if (latitudes.Count != longitudes.Count)
        {
            error = $"'{latitudeName}' holds {latitudes.Count} sub-grid(s) and its '{longitudeName}' holds {longitudes.Count}.";
            return false;
        }

        var joined = new List<DatumShiftGrid>(latitudes.Count);
        for (var index = 0; index < latitudes.Count; index++)
        {
            if (!SameBlock(latitudes[index].Block, longitudes[index].Block))
            {
                error = $"'{latitudeName}' and '{longitudeName}' describe different blocks, so they are not one grid.";
                return false;
            }

            // The .los half states its longitude shifts positive west, because
            // NADCON's method was built for NAD27's positive-west longitudes
            // while the datums it reaches are positive east — EPSG's own note
            // on operations 1241, 1243 and 15864 says so, and PROJ 9.8.1
            // negates these same values in NTv1Grid::valueAt for the nadcon
            // file type. The half is negated here, where the two halves are
            // joined into one grid, so nothing downstream of the reader has to
            // know which way round the container kept its sign.
            var longitudeShifts = longitudes[index].Shifts;
            for (var node = 0; node < longitudeShifts.Length; node++)
            {
                longitudeShifts[node] = -longitudeShifts[node];
            }

            var block = latitudes[index].Block;
            joined.Add(new DatumShiftGrid(
                block.Name,
                latitudeName,
                GridFormat.Nadcon,
                block.SouthLatitude,
                block.NorthLatitude,
                block.WestLongitude,
                block.EastLongitude,
                block.LatitudeIncrement,
                block.LongitudeIncrement,
                accuracyMetres,
                latitudes[index].Shifts,
                longitudes[index].Shifts));
        }

        error = null;
        grids = joined;
        return true;
    }

    /// <summary>Reads a deployed pair from disk.</summary>
    public static bool TryRead(
        string latitudePath,
        string longitudePath,
        double accuracyMetres,
        out IReadOnlyList<DatumShiftGrid> grids,
        out string? error)
    {
        grids = [];
        var latitudeName = Path.GetFileName(latitudePath);
        var longitudeName = Path.GetFileName(longitudePath);

        // Both halves or neither: a pair is one grid, and a lone file is a
        // deployment that was only half carried out.
        if (!File.Exists(latitudePath) || !File.Exists(longitudePath))
        {
            error = $"A NADCON grid is a pair: '{latitudeName}' and '{longitudeName}' must both be in the configured grid directory.";
            return false;
        }

        try
        {
            return TryRead(
                File.ReadAllBytes(latitudePath),
                latitudeName,
                File.ReadAllBytes(longitudePath),
                longitudeName,
                accuracyMetres,
                out grids,
                out error);
        }
        catch (IOException exception)
        {
            grids = [];
            error = $"The grid file '{latitudeName}' could not be read: {exception.Message}";
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            grids = [];
            error = $"The grid file '{latitudeName}' could not be read: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// One half of a pair: the blocks its headers describe and the shifts its
    /// own file carries, kept apart rather than joined, because the join is
    /// what has to be checked rather than assumed.
    /// </summary>
    private static bool TryReadFile(byte[] bytes, out List<Half> halves, out string? error)
    {
        halves = [];
        var offset = 0;
        if (!TryReadCounts(bytes, ref offset, out var subGridHeaderSize, out var subGridCount, out error))
        {
            return false;
        }

        while (offset < bytes.Length)
        {
            // A record where a sub-grid header would start may instead close
            // the file, so the record is read to find out which and rewound if
            // it is a header after all.
            var recordStart = offset;
            if (!TryReadRecord(bytes, ref offset, out var key, out _))
            {
                error = $"The NADCON file ends inside a header record.";
                return false;
            }

            if (IsKey(key, EndKey))
            {
                break;
            }

            offset = recordStart;
            var half = TryReadSubGrid(bytes, ref offset, subGridHeaderSize, out error);
            if (half is null)
            {
                return false;
            }

            halves.Add(half.Value);
        }

        if (halves.Count != subGridCount)
        {
            error = $"The NADCON file declares {subGridCount} sub-grid(s) but its headers hold {halves.Count}.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>The overview's three counts: how many records each header is wide, and how many sub-grids follow.</summary>
    private static bool TryReadCounts(byte[] bytes, ref int offset, out int subGridHeaderSize, out int subGridCount, out string? error)
    {
        subGridHeaderSize = 0;
        subGridCount = 0;
        var overview = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < OverviewRecordCount; index++)
        {
            if (!TryReadRecord(bytes, ref offset, out var key, out var value))
            {
                error = "The file is shorter than one NADCON overview header.";
                return false;
            }

            if (index == 0 && !IsKey(key, OverviewKey))
            {
                error = "The file does not begin with a NADCON overview header (NUMOREC).";
                return false;
            }

            overview[key] = value;
        }

        if (!TryCount(overview, OverviewKey, out var overviewRecords)
            || overviewRecords < OverviewRecordCount)
        {
            error = $"The NADCON overview header declares {OverviewRecordCount} records but does not hold them.";
            return false;
        }

        if (!TryCount(overview, SubGridRecordCountKey, out subGridHeaderSize)
            || !TryCount(overview, SubGridCountKey, out subGridCount)
            || subGridHeaderSize <= 0
            || subGridCount <= 0)
        {
            error = "The NADCON overview header's record counts are missing or nonsensical.";
            return false;
        }

        subGridHeaderSize *= RecordLength;
        error = null;
        return true;
    }

    /// <summary>
    /// One sub-grid: the identity record that names it, the numbers its header
    /// states, the shifts its own file carries, and the three cross-checks
    /// between them.
    /// </summary>
    private static Half? TryReadSubGrid(byte[] bytes, ref int offset, int subGridHeaderSize, out string? error)
    {
        error = null;
        var headerEnd = offset + subGridHeaderSize;
        var name = string.Empty;
        var numbers = new List<double>();
        while (offset < headerEnd)
        {
            if (!TryReadRecord(bytes, ref offset, out var key, out var value))
            {
                error = "A NADCON sub-grid header stops before its last record.";
                return null;
            }

            if (IsKey(key, IdentityKey))
            {
                name = Clean(value);
                continue;
            }

            // Everything that is not the identity contributes numbers where it
            // states them; the text between them — a source, a date, a unit —
            // is not this reader's business.
            foreach (var token in value.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    numbers.Add(number);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "A NADCON sub-grid header does not name itself (ID).";
            return null;
        }

        if (numbers.Count != BlockNumberCount)
        {
            error = $"The NADCON sub-grid '{name}' states {numbers.Count} numbers for its block where {BlockNumberCount} are expected.";
            return null;
        }

        if (!TryBlock(name, numbers, out var block, out error))
        {
            return null;
        }

        if (!TryReadShifts(bytes, ref offset, block, out var shifts, out error))
        {
            return null;
        }

        return new Half(block, shifts);
    }

    /// <summary>
    /// Reads the node block into south-first, west-first order. NADCON walks its
    /// rows from the north down, so the file order is reversed as it is read —
    /// the block would otherwise be the wrong way up, and a grid that is the
    /// wrong way up shifts every coordinate in it by the wrong amount.
    /// </summary>
    private static bool TryReadShifts(byte[] bytes, ref int offset, Block block, out double[] shifts, out string? error)
    {
        shifts = [];
        var end = offset + (block.Rows * block.Columns * BytesPerNode);
        if (end > bytes.Length)
        {
            error = $"The NADCON sub-grid '{block.Name}' declares {block.Rows * block.Columns} shifts but the file holds fewer than that.";
            return false;
        }

        var read = new double[block.Rows * block.Columns];
        for (var row = 0; row < block.Rows; row++)
        {
            for (var column = 0; column < block.Columns; column++)
            {
                var target = ((block.Rows - 1 - row) * block.Columns) + column;
                read[target] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset, BytesPerNode));
                offset += BytesPerNode;
            }
        }

        shifts = read;
        error = null;
        return true;
    }

    /// <summary>
    /// The block a sub-grid header describes, read three times over and
    /// required to agree with itself: the edges against the increments, the
    /// node counts against the shape those imply, and the anchor corner
    /// against the block. The edges are ordered rather than trusted, so a
    /// header written north-to-south reads the same way as one written
    /// south-to-north, and every longitude in it is negated out of the
    /// container's positive-west convention.
    /// </summary>
    private static bool TryBlock(string name, List<double> numbers, out Block block, out string? error)
    {
        block = default;
        var (south, north) = (numbers[0], numbers[1]);

        // Every longitude a NADCON header states is positive west, for the
        // same reason the .los values are: the edges of a block, the corner
        // the increments are measured from and the shifts tabulated over it
        // are all in the convention of the datum the grid was built from, not
        // the datum it reaches. Negated on the way in, so the block the grid
        // is built over is the block the datums are tabulated over.
        (var east, var west) = (-numbers[2], -numbers[3]);
        var latitudeIncrement = Math.Abs(numbers[4]);
        var longitudeIncrement = Math.Abs(numbers[5]);
        var rows = (int)Math.Round(numbers[6]);
        var columns = (int)Math.Round(numbers[7]);
        var (anchorLatitude, anchorLongitude) = (numbers[8], -numbers[9]);

        (south, north) = (Math.Min(south, north), Math.Max(south, north));
        (west, east) = (Math.Min(west, east), Math.Max(west, east));

        if (latitudeIncrement == 0.0 || longitudeIncrement == 0.0)
        {
            error = $"The NADCON sub-grid '{name}' declares a zero increment, so its block has no nodes.";
            return false;
        }

        var impliedRows = (int)Math.Round((north - south) / latitudeIncrement) + 1;
        var impliedColumns = (int)Math.Round((east - west) / longitudeIncrement) + 1;
        if (rows != impliedRows || columns != impliedColumns)
        {
            error = $"The NADCON sub-grid '{name}' states {rows} by {columns} nodes, but its edges and {latitudeIncrement} by {longitudeIncrement} degree increments describe {impliedRows} by {impliedColumns}.";
            return false;
        }

        if (rows < 2 || columns < 2)
        {
            error = $"The NADCON sub-grid '{name}' states a {rows} by {columns} block, which has no cell to interpolate from.";
            return false;
        }

        if (!Same(anchorLatitude, south) || !Same(anchorLongitude, west))
        {
            error = $"The NADCON sub-grid '{name}' anchors its block at {anchorLatitude}, {anchorLongitude}, which is not its south-west corner.";
            return false;
        }

        var spanLatitude = south + ((rows - 1) * latitudeIncrement);
        var spanLongitude = west + ((columns - 1) * longitudeIncrement);
        if (!Same(spanLatitude, north) || !Same(spanLongitude, east))
        {
            error = $"The NADCON sub-grid '{name}' counts {rows} by {columns} nodes, which does not reach its stated north and east edges from its anchor.";
            return false;
        }

        block = new Block(
            name, south, north, west, east, latitudeIncrement, longitudeIncrement, rows, columns);
        error = null;
        return true;
    }

    /// <summary>
    /// Whether two halves of a pair are the same grid. Everything that decides
    /// the lattice has to match; the shifts themselves cannot, since they are
    /// the point of the split.
    /// </summary>
    private static bool SameBlock(Block left, Block right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && Same(left.SouthLatitude, right.SouthLatitude)
        && Same(left.NorthLatitude, right.NorthLatitude)
        && Same(left.WestLongitude, right.WestLongitude)
        && Same(left.EastLongitude, right.EastLongitude)
        && Same(left.LatitudeIncrement, right.LatitudeIncrement)
        && Same(left.LongitudeIncrement, right.LongitudeIncrement)
        && left.Rows == right.Rows
        && left.Columns == right.Columns;

    private static bool Same(double left, double right) => Math.Abs(left - right) <= EdgeTolerance;

    private static bool TryReadRecord(byte[] bytes, ref int offset, out string key, out string value)
    {
        key = value = string.Empty;
        if (offset + RecordLength > bytes.Length)
        {
            return false;
        }

        key = Clean(Encoding.ASCII.GetString(bytes, offset, 8));
        value = Encoding.ASCII.GetString(bytes, offset + 8, RecordLength - 8);
        offset += RecordLength;
        return true;
    }

    /// <summary>Strips the padding a record carries, whether it is spaces or NUL bytes.</summary>
    private static string Clean(string field) => field.Trim(' ', '\0', '\t', '\r', '\n');

    private static bool IsKey(string key, string expected) => string.Equals(key, expected, StringComparison.Ordinal);

    private static bool TryCount(Dictionary<string, string> header, string key, out int value)
    {
        value = 0;
        return header.TryGetValue(key, out var raw)
            && int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>What separates the numbers inside a header record.</summary>
    private static readonly char[] Separators = [' ', '\0', '\t', '\r', '\n', ','];

    /// <summary>One sub-grid of one half: the block it covers and the shifts its file carries.</summary>
    private readonly record struct Half(Block Block, double[] Shifts);

    /// <summary>The block a sub-grid header describes, cross-checked and normalised.</summary>
    private readonly record struct Block(
        string Name,
        double SouthLatitude,
        double NorthLatitude,
        double WestLongitude,
        double EastLongitude,
        double LatitudeIncrement,
        double LongitudeIncrement,
        int Rows,
        int Columns);
}
