namespace Spatial.Core.Geometry;

/// <summary>A collection of polygons.</summary>
public sealed class MultiPolygon : GeometryCollectionBase<Polygon>, IMultiPolygon, IEquatable<MultiPolygon>
{
    public MultiPolygon(IEnumerable<Polygon> polygons, CoordinateReference? coordinateReference = null)
        : base(polygons, coordinateReference)
    {
    }

    public IReadOnlyList<Polygon> Polygons => Children;

    public override GeometryType Type => GeometryType.MultiPolygon;

    public bool Equals(MultiPolygon? other) => other is not null && ContentEquals(other);

    public override bool Equals(object? obj) => obj is MultiPolygon other && ContentEquals(other);

    private bool ContentEquals(MultiPolygon other) =>
        Nullable.Equals(CoordinateReference, other.CoordinateReference)
        && Polygons.SequenceEqual(other.Polygons);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CoordinateReference);
        foreach (var polygon in Polygons)
        {
            hash.Add(polygon);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => $"MultiPolygon [{Polygons.Count} polygons]";
}
