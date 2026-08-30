namespace Spatial.Core.Geometry;

/// <summary>
/// Axis-aligned bounding rectangle, computed from coordinate minima and
/// maxima (structural core behaviour). Immutable.
///
/// <see cref="Empty"/> is the envelope of a geometry with no coordinates.
/// The default value of the struct is a degenerate envelope at the origin and
/// must not be used to mean "empty". Bounds must be finite: NaN ordinates are
/// skipped by the computed creators (<see cref="FromCoordinates"/> and
/// <see cref="FromSequence"/>), while explicit construction rejects NaN and
/// infinite bounds.
/// </summary>
public readonly struct Envelope : IEquatable<Envelope>
{
    /// <summary>The envelope of a geometry with no coordinates.</summary>
    public static Envelope Empty { get; } = new(true);

    private readonly double _minX;
    private readonly double _minY;
    private readonly double _maxX;
    private readonly double _maxY;

    public Envelope(double minX, double minY, double maxX, double maxY)
    {
        if (double.IsNaN(minX) || double.IsNaN(minY) || double.IsNaN(maxX) || double.IsNaN(maxY)
            || double.IsInfinity(minX) || double.IsInfinity(minY) || double.IsInfinity(maxX) || double.IsInfinity(maxY))
        {
            throw new ArgumentException(
                FormattableString.Invariant($"Envelope bounds must be finite, got ({minX}, {minY}) to ({maxX}, {maxY})."));
        }

        if (minX > maxX || minY > maxY)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"Invalid envelope: minimum ({minX}, {minY}) exceeds maximum ({maxX}, {maxY})."));
        }

        _minX = minX;
        _minY = minY;
        _maxX = maxX;
        _maxY = maxY;
    }

    private Envelope(bool empty)
    {
        _minX = double.PositiveInfinity;
        _minY = double.PositiveInfinity;
        _maxX = double.NegativeInfinity;
        _maxY = double.NegativeInfinity;
    }

    public double MinX => _minX;

    public double MinY => _minY;

    public double MaxX => _maxX;

    public double MaxY => _maxY;

    public bool IsEmpty => _minX > _maxX;

    /// <summary>Width, or 0 for <see cref="Empty"/>.</summary>
    public double Width => IsEmpty ? 0 : _maxX - _minX;

    /// <summary>Height, or 0 for <see cref="Empty"/>.</summary>
    public double Height => IsEmpty ? 0 : _maxY - _minY;

    public double CenterX => (_minX + _maxX) / 2;

    public double CenterY => (_minY + _maxY) / 2;

    /// <summary>
    /// Computes the envelope covering the coordinates. Coordinates with a NaN
    /// X or Y ordinate are skipped; an empty span or all-skipped coordinates
    /// yield <see cref="Empty"/>.
    /// </summary>
    public static Envelope FromCoordinates(ReadOnlySpan<Coordinate> coordinates)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var found = false;

        foreach (var coordinate in coordinates)
        {
            Expand(ref minX, ref minY, ref maxX, ref maxY, ref found, coordinate.X, coordinate.Y);
        }

        return found ? new Envelope(minX, minY, maxX, maxY) : Empty;
    }

    /// <summary>
    /// Computes the envelope covering the sequence ordinates, using the same
    /// NaN-skipping rule as <see cref="FromCoordinates"/>.
    /// </summary>
    public static Envelope FromSequence(ICoordinateSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        var found = false;

        for (var i = 0; i < sequence.Count; i++)
        {
            Expand(ref minX, ref minY, ref maxX, ref maxY, ref found, sequence.GetOrdinate(i, Ordinate.X), sequence.GetOrdinate(i, Ordinate.Y));
        }

        return found ? new Envelope(minX, minY, maxX, maxY) : Empty;
    }

    /// <summary>Whether <paramref name="x"/>, <paramref name="y"/> lie inside the envelope (boundaries included).</summary>
    public bool Contains(double x, double y) =>
        !IsEmpty && x >= _minX && x <= _maxX && y >= _minY && y <= _maxY;

    /// <summary>Whether the coordinate lies inside the envelope (boundaries included).</summary>
    public bool Contains(Coordinate coordinate) => Contains(coordinate.X, coordinate.Y);

    /// <summary>Whether <paramref name="other"/> lies entirely inside this envelope (boundaries included).</summary>
    public bool Contains(in Envelope other) =>
        !IsEmpty && !other.IsEmpty
        && _minX <= other._minX && other._maxX <= _maxX
        && _minY <= other._minY && other._maxY <= _maxY;

    /// <summary>Whether the two envelopes share any point (touching boundaries count).</summary>
    public bool Intersects(in Envelope other) =>
        !IsEmpty && !other.IsEmpty
        && _minX <= other._maxX && other._minX <= _maxX
        && _minY <= other._maxY && other._minY <= _maxY;

    /// <summary>The smallest envelope containing both this and <paramref name="other"/>.</summary>
    public Envelope Union(in Envelope other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        return new Envelope(
            Math.Min(_minX, other._minX),
            Math.Min(_minY, other._minY),
            Math.Max(_maxX, other._maxX),
            Math.Max(_maxY, other._maxY));
    }

    public bool Equals(Envelope other) =>
        _minX == other._minX && _minY == other._minY && _maxX == other._maxX && _maxY == other._maxY;

    public override bool Equals(object? obj) => obj is Envelope other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_minX, _minY, _maxX, _maxY);

    public static bool operator ==(Envelope left, Envelope right) => left.Equals(right);

    public static bool operator !=(Envelope left, Envelope right) => !left.Equals(right);

    public override string ToString() =>
        IsEmpty ? "Envelope [empty]" :
        FormattableString.Invariant($"Envelope [{_minX}, {_minY}] to [{_maxX}, {_maxY}]");

    private static void Expand(ref double minX, ref double minY, ref double maxX, ref double maxY, ref bool found, double x, double y)
    {
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return;
        }

        found = true;
        minX = Math.Min(minX, x);
        minY = Math.Min(minY, y);
        maxX = Math.Max(maxX, x);
        maxY = Math.Max(maxY, y);
    }
}
