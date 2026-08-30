namespace Spatial.Core.Geometry;

/// <summary>
/// A two-dimensional geometry: one exterior ring (a closed
/// <see cref="LineString"/>) plus any interior rings. Ring closure,
/// orientation and self-intersection are not validated here — validation is a
/// plugin capability.
/// </summary>
public sealed class Polygon : IGeometry, IEquatable<Polygon>
{
    private readonly LineString _exteriorRing;
    private readonly LineString[] _interiorRings;
    private readonly CoordinateReference? _coordinateReference;

    public Polygon(LineString exteriorRing, IEnumerable<LineString>? interiorRings = null, CoordinateReference? coordinateReference = null)
    {
        ArgumentNullException.ThrowIfNull(exteriorRing);

        _exteriorRing = exteriorRing;
        _interiorRings = interiorRings?.ToArray() ?? [];
        _coordinateReference = coordinateReference;
    }

    public LineString ExteriorRing => _exteriorRing;

    public IReadOnlyList<LineString> InteriorRings => _interiorRings;

    public GeometryType Type => GeometryType.Polygon;

    public CoordinateLayout Layout => GeometryAggregates.MergeLayouts(Rings);

    public CoordinateReference? CoordinateReference => _coordinateReference;

    /// <summary>A polygon is empty exactly when its exterior ring is empty.</summary>
    public bool IsEmpty => _exteriorRing.IsEmpty;

    public int CoordinateCount => _exteriorRing.CoordinateCount + GeometryAggregates.SumCoordinateCounts(_interiorRings);

    public Envelope? Envelope => GeometryAggregates.MergeEnvelopes(Rings);

    private IEnumerable<LineString> Rings
    {
        get
        {
            yield return _exteriorRing;
            foreach (var ring in _interiorRings)
            {
                yield return ring;
            }
        }
    }

    public bool Equals(Polygon? other) =>
        other is not null
        && Nullable.Equals(_coordinateReference, other._coordinateReference)
        && _exteriorRing.Equals(other._exteriorRing)
        && _interiorRings.SequenceEqual(other._interiorRings);

    public override bool Equals(object? obj) => obj is Polygon other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_coordinateReference);
        hash.Add(_exteriorRing);
        foreach (var ring in _interiorRings)
        {
            hash.Add(ring);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => $"Polygon [1 exterior ring, {_interiorRings.Length} interior rings]";
}
