namespace Spatial.Transformations.ProjNet.Grids;

/// <summary>
/// A loaded datum-shift grid: a block of ground over which the shift from one
/// datum to another is tabulated at every node, and nothing outside it.
/// <para>
/// This is the value the registry hands out and the graph publishes, so a
/// grid is a thing with a name, a file it came from, a format, a coverage and
/// an accuracy — not a bare function. Outside the block a grid has no answer
/// and says so (<see cref="Covers"/>) rather than extrapolating: the edge of a
/// grid is a real boundary of the data, and a survey-grade answer that
/// quietly smooths across it is worse than one that admits the point is
/// outside.
/// </para>
/// <para>
/// Shifts are held in seconds of arc and added in degrees, interpolated
/// bilinearly from the four nodes around the point. The interpolation uses
/// the point's own fractional position inside its cell rather than snapping to
/// a node first, which is the convention the reference implementation uses; a
/// point exactly on a node therefore gets that node's own shift.
/// </para>
/// </summary>
internal sealed class DatumShiftGrid
{
    private const double SecondsToDegrees = 1.0 / 3600.0;

    /// <summary>
    /// How many times the inverse re-evaluates the forward shift. The inverse
    /// of a tabulated shift has no closed form, so it is found by iteration:
    /// guess the source, shift it, correct, and stop when the correction stops
    /// moving. The bound is here so the loop is finite; a published grid's
    /// shift is well under a metre, which two passes already resolve far
    /// inside the tolerance the loop stops at.
    /// </summary>
    private const int InversePasses = 8;

    /// <summary>How close the inverse has to get before it stops iterating.</summary>
    private const double InverseTolerance = 1e-12;

    private readonly double[] _latitudeShifts;
    private readonly double[] _longitudeShifts;
    private readonly double _southLatitude;
    private readonly double _westLongitude;
    private readonly double _latitudeIncrement;
    private readonly double _longitudeIncrement;

    /// <summary>Nodes across a row of the block, after normalising the file's column order.</summary>
    private readonly int _nodesPerRow;

    /// <summary>Nodes down a column of the block. A block is not square, so this is tracked apart from the row count.</summary>
    private readonly int _nodesPerColumn;

    public DatumShiftGrid(
        string name,
        string fileName,
        GridFormat format,
        double southLatitude,
        double northLatitude,
        double westLongitude,
        double eastLongitude,
        double latitudeIncrement,
        double longitudeIncrement,
        double accuracyMetres,
        double[] latitudeShifts,
        double[] longitudeShifts)
    {
        Name = name;
        FileName = fileName;
        Format = format;
        YMin = southLatitude;
        YMax = northLatitude;
        XMin = westLongitude;
        XMax = eastLongitude;
        AccuracyMetres = accuracyMetres;
        _latitudeShifts = latitudeShifts;
        _longitudeShifts = longitudeShifts;
        _southLatitude = southLatitude;
        _westLongitude = westLongitude;
        _latitudeIncrement = latitudeIncrement;
        _longitudeIncrement = longitudeIncrement;
        _nodesPerRow = (int)Math.Round((eastLongitude - westLongitude) / longitudeIncrement) + 1;
        _nodesPerColumn = (int)Math.Round((northLatitude - southLatitude) / latitudeIncrement) + 1;
    }

    /// <summary>The sub-grid's own name — what a published operation names the grid by.</summary>
    public string Name { get; }

    /// <summary>
    /// The file the grid was read from, by name only: a published operation
    /// says which bundle served it without publishing where on the host's disk
    /// that host keeps its grids.
    /// </summary>
    public string FileName { get; }

    public GridFormat Format { get; }

    /// <summary>The southern edge of the block, in degrees.</summary>
    public double YMin { get; }

    /// <summary>The northern edge of the block, in degrees.</summary>
    public double YMax { get; }

    /// <summary>The western edge of the block, in degrees.</summary>
    public double XMin { get; }

    /// <summary>The eastern edge of the block, in degrees.</summary>
    public double XMax { get; }

    /// <summary>
    /// The grid's own accuracy in metres, as the worst of the per-node
    /// accuracies the file tabulates. A grid is only as good as its worst node,
    /// so that is the number a published operation's accuracy is derived from,
    /// rather than a round number chosen for the operation.
    /// </summary>
    public double AccuracyMetres { get; }

    /// <summary>
    /// Whether the grid can answer for a coordinate. A point is interpolated
    /// from a cell of four nodes, so it has to have a whole cell around it: a
    /// point on the outermost row or column of nodes is the far edge of the
    /// last cell rather than a point inside one, and there is nothing beyond
    /// it to interpolate from. Such a point is outside the grid, not on it.
    /// </summary>
    public bool Covers(double longitude, double latitude) =>
        Cell(longitude, latitude) is not null;

    /// <summary>
    /// The forward shift: the coordinate moved from the grid's own datum to
    /// the datum the grid was built to reach. The outputs are in degrees, and
    /// are the input untouched when the point is outside the block.
    /// </summary>
    public bool TryShiftForward(double longitude, double latitude, out double shiftedLatitude, out double shiftedLongitude)
    {
        if (Interpolate(longitude, latitude, out var latitudeShift, out var longitudeShift))
        {
            shiftedLatitude = latitude + (latitudeShift * SecondsToDegrees);
            shiftedLongitude = longitude + (longitudeShift * SecondsToDegrees);
            return true;
        }

        shiftedLatitude = latitude;
        shiftedLongitude = longitude;
        return false;
    }

    /// <summary>
    /// The inverse shift: the coordinate moved back from the datum the grid
    /// reaches to the grid's own datum. A tabulated shift is not analytically
    /// invertible, so this iterates the forward shift to a fixed point — a
    /// forward and inverse that disagreed would report a round trip that did
    /// not close, which is exactly what the round-trip test measures.
    /// </summary>
    public bool TryShiftInverse(double longitude, double latitude, out double shiftedLatitude, out double shiftedLongitude)
    {
        var sourceLongitude = longitude;
        var sourceLatitude = latitude;
        for (var pass = 0; pass < InversePasses; pass++)
        {
            if (!TryShiftForward(sourceLongitude, sourceLatitude, out var forwardLatitude, out var forwardLongitude))
            {
                shiftedLatitude = latitude;
                shiftedLongitude = longitude;
                return false;
            }

            var correctionLatitude = latitude - forwardLatitude;
            var correctionLongitude = longitude - forwardLongitude;
            sourceLatitude += correctionLatitude;
            sourceLongitude += correctionLongitude;
            if (Math.Abs(correctionLatitude) + Math.Abs(correctionLongitude) < InverseTolerance)
            {
                break;
            }
        }

        shiftedLatitude = sourceLatitude;
        shiftedLongitude = sourceLongitude;
        return true;
    }

    /// <summary>
    /// The shift at a point, bilinearly interpolated from the four nodes
    /// around it. False when the point is outside the block.
    /// </summary>
    private bool Interpolate(double longitude, double latitude, out double latitudeShift, out double longitudeShift)
    {
        if (Cell(longitude, latitude) is not { } cell)
        {
            latitudeShift = 0.0;
            longitudeShift = 0.0;
            return false;
        }

        var (row, column, latitudeFraction, longitudeFraction) = cell;

        var (latitudeComplement, longitudeComplement) = (1.0 - latitudeFraction, 1.0 - longitudeFraction);
        var southWest = Index(row, column);
        var (southEast, northWest, northEast) = (southWest + 1, southWest + _nodesPerRow, southWest + _nodesPerRow + 1);
        var (southWeight, northWeight) = (latitudeComplement, latitudeFraction);

        latitudeShift = Weighted(
            _latitudeShifts,
            southWest, southEast, northWest, northEast,
            southWeight, northWeight, longitudeComplement, longitudeFraction);
        longitudeShift = Weighted(
            _longitudeShifts,
            southWest, southEast, northWest, northEast,
            southWeight, northWeight, longitudeComplement, longitudeFraction);
        return true;
    }

    /// <summary>
    /// The bilinear blend of four corner shifts: the south edge of the cell by
    /// how far south the point sits, the north edge by the rest, and each
    /// edge across by the point's position between the two columns.
    /// </summary>
    private static double Weighted(
        double[] shifts,
        int southWest,
        int southEast,
        int northWest,
        int northEast,
        double southWeight,
        double northWeight,
        double longitudeComplement,
        double longitudeFraction) =>
        (shifts[southWest] * southWeight * longitudeComplement)
        + (shifts[southEast] * southWeight * longitudeFraction)
        + (shifts[northWest] * northWeight * longitudeComplement)
        + (shifts[northEast] * northWeight * longitudeFraction);

    /// <summary>
    /// The cell a point falls in: its row and column, and how far into each it
    /// is, or null when the point is outside the block. Rows are counted from
    /// the south, which is the order the reader normalises the file into.
    /// </summary>
    private (int Row, int Column, double LatitudeFraction, double LongitudeFraction)? Cell(double longitude, double latitude)
    {
        var row = (latitude - _southLatitude) / _latitudeIncrement;
        var column = (longitude - _westLongitude) / _longitudeIncrement;
        if (double.IsNaN(row) || double.IsNaN(column)
            || row < 0.0 || row >= _nodesPerColumn - 1
            || column < 0.0 || column >= _nodesPerRow - 1)
        {
            return null;
        }

        var rowIndex = (int)Math.Floor(row);
        var columnIndex = (int)Math.Floor(column);
        return (rowIndex, columnIndex, row - rowIndex, column - columnIndex);
    }

    private int Index(int row, int column) => (row * _nodesPerRow) + column;
}
