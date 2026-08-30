namespace Spatial.Core.Geometry;

/// <summary>
/// Structural base for composite geometries (multi-types and
/// <see cref="GeometryCollection"/>): defensive child storage, merged layout,
/// emptiness, coordinate sums and merged envelopes.
/// </summary>
public abstract class GeometryCollectionBase<TChild> : IGeometry
    where TChild : class, IGeometry
{
    private readonly TChild[] _children;
    private readonly CoordinateReference? _coordinateReference;
    private readonly CoordinateLayout _layout;

    protected GeometryCollectionBase(IEnumerable<TChild> children, CoordinateReference? coordinateReference)
    {
        ArgumentNullException.ThrowIfNull(children);

        _children = children.ToArray();
        _coordinateReference = coordinateReference;
        _layout = GeometryAggregates.MergeLayouts(_children);
    }

    /// <summary>Defensively copied children.</summary>
    protected IReadOnlyList<TChild> Children => _children;

    public abstract GeometryType Type { get; }

    /// <summary>The most expressive layout across all children.</summary>
    public CoordinateLayout Layout => _layout;

    public CoordinateReference? CoordinateReference => _coordinateReference;

    /// <summary>Empty when there are no children or every child is empty.</summary>
    public bool IsEmpty => _children.Length == 0 || GeometryAggregates.AllEmpty(_children);

    public int CoordinateCount => GeometryAggregates.SumCoordinateCounts(_children);

    public Envelope? Envelope => GeometryAggregates.MergeEnvelopes(_children);
}
