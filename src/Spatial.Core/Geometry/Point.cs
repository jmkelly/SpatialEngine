
namespace Spatial.Core.Geometry;

/// <summary>
/// A zero-dimensional geometry: a single coordinate, or empty. The layout is
/// derived from the coordinate's ordinates; an empty point carries
/// <see cref="CoordinateLayout.Xy"/> unless created through the codec or
/// <see cref="GeometryFactory.CreateEmptyPoint"/> (which honours an explicit
/// layout).
/// </summary>
public sealed class Point : IPoint, IEquatable<Point>
{
    private readonly Coordinate? _coordinate;
    private readonly CoordinateReference? _coordinateReference;
    private readonly CoordinateLayout _layout;

    public Point(Coordinate? coordinate, CoordinateReference? coordinateReference = null)
    {
        _coordinate = coordinate;
        _coordinateReference = coordinateReference;
        _layout = coordinate is { } value ? DeriveLayout(value) : CoordinateLayout.Xy;
    }

    internal Point(Coordinate? coordinate, CoordinateReference? coordinateReference, CoordinateLayout layout)
    {
        _coordinate = coordinate;
        _coordinateReference = coordinateReference;
        _layout = layout;
    }

    /// <summary>The coordinate, or <c>null</c> when empty.</summary>
    public Coordinate? Coordinate => _coordinate;

    public double? X => _coordinate?.X;

    public double? Y => _coordinate?.Y;

    public double? Z => _coordinate?.Z;

    public double? M => _coordinate?.M;

    public GeometryType Type => GeometryType.Point;

    public CoordinateLayout Layout => _layout;

    public CoordinateReference? CoordinateReference => _coordinateReference;

    public bool IsEmpty => _coordinate is null;

    public int CoordinateCount => _coordinate is null ? 0 : 1;

    /// <summary>A degenerate envelope at the point, or <c>null</c> when empty or NaN-valued.</summary>
    public Envelope? Envelope
    {
        get
        {
            if (_coordinate is not { } coordinate || double.IsNaN(coordinate.X) || double.IsNaN(coordinate.Y))
            {
                return null;
            }

            return new Envelope(coordinate.X, coordinate.Y, coordinate.X, coordinate.Y);
        }
    }

    public bool Equals(Point? other) =>
        other is not null
        && _layout == other._layout
        && Nullable.Equals(_coordinate, other._coordinate)
        && Nullable.Equals(_coordinateReference, other._coordinateReference);

    public override bool Equals(object? obj) => obj is Point other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_coordinate, _coordinateReference, _layout);

    public override string ToString() =>
        _coordinate is { } coordinate
            ? FormattableString.Invariant($"Point ({coordinate.X}, {coordinate.Y})")
            : "Point (empty)";

    private static CoordinateLayout DeriveLayout(Coordinate coordinate) => (coordinate.Z is not null, coordinate.M is not null) switch
    {
        (true, true) => CoordinateLayout.Xyzm,
        (true, false) => CoordinateLayout.Xyz,
        (false, true) => CoordinateLayout.Xym,
        (false, false) => CoordinateLayout.Xy,
    };
}
