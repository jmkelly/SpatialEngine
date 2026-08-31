namespace Spatial.Core.Geometry;

/// <summary>A collection of points.</summary>
public sealed class MultiPoint : GeometryCollectionBase<Point>, IMultiPoint, IEquatable<MultiPoint>
{
    public MultiPoint(IEnumerable<Point> points, CoordinateReference? coordinateReference = null)
        : base(points, coordinateReference)
    {
    }

    public IReadOnlyList<Point> Points => Children;

    public override GeometryType Type => GeometryType.MultiPoint;

    public bool Equals(MultiPoint? other) =>
        other is not null
        && Nullable.Equals(CoordinateReference, other.CoordinateReference)
        && Points.SequenceEqual(other.Points);

    public override bool Equals(object? obj) => obj is MultiPoint other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CoordinateReference);
        foreach (var point in Points)
        {
            hash.Add(point);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => $"MultiPoint [{Points.Count} points]";
}
