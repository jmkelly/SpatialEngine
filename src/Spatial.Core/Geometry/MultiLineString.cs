namespace Spatial.Core.Geometry;

/// <summary>A collection of line strings.</summary>
public sealed class MultiLineString : GeometryCollectionBase<LineString>, IMultiLineString, IEquatable<MultiLineString>
{
    public MultiLineString(IEnumerable<LineString> lineStrings, CoordinateReference? coordinateReference = null)
        : base(lineStrings, coordinateReference)
    {
    }

    public IReadOnlyList<LineString> LineStrings => Children;

    public override GeometryType Type => GeometryType.MultiLineString;

    public bool Equals(MultiLineString? other) =>
        other is not null
        && Nullable.Equals(CoordinateReference, other.CoordinateReference)
        && LineStrings.SequenceEqual(other.LineStrings);

    public override bool Equals(object? obj) => obj is MultiLineString other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CoordinateReference);
        foreach (var lineString in LineStrings)
        {
            hash.Add(lineString);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => $"MultiLineString [{LineStrings.Count} line strings]";
}
