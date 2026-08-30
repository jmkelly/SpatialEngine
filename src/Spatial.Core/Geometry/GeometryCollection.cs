using System.Diagnostics.CodeAnalysis;

namespace Spatial.Core.Geometry;

/// <summary>A heterogeneous collection of geometries.</summary>
[SuppressMessage("Naming", "CA1711", Justification = "'GeometryCollection' is the OGC simple-feature term for this core type (plan §7); no reserved suffix conflict is intended.")]
public sealed class GeometryCollection : GeometryCollectionBase<IGeometry>, IEquatable<GeometryCollection>
{
    public GeometryCollection(IEnumerable<IGeometry> geometries, CoordinateReference? coordinateReference = null)
        : base(geometries, coordinateReference)
    {
    }

    public IReadOnlyList<IGeometry> Geometries => Children;

    public override GeometryType Type => GeometryType.GeometryCollection;

    public bool Equals(GeometryCollection? other) =>
        other is not null
        && Nullable.Equals(CoordinateReference, other.CoordinateReference)
        && Geometries.SequenceEqual(other.Geometries, GeometryComparer.Instance);

    public override bool Equals(object? obj) => obj is GeometryCollection other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(CoordinateReference);
        foreach (var geometry in Geometries)
        {
            hash.Add(GeometryComparer.GetHashCode(geometry));
        }

        return hash.ToHashCode();
    }

    public override string ToString() => $"GeometryCollection [{Geometries.Count} parts]";
}
