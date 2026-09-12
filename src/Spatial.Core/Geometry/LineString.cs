
namespace Spatial.Core.Geometry;

/// <summary>
/// A one-dimensional geometry: an ordered coordinate sequence. Closure is not
/// enforced — ring validation is a plugin verb, not structural core
/// behaviour.
/// </summary>
public sealed class LineString : ILineString, IEquatable<LineString>
{
    private readonly ICoordinateSequence _sequence;
    private readonly CoordinateReference? _coordinateReference;

    public LineString(ICoordinateSequence sequence, CoordinateReference? coordinateReference = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        _sequence = sequence;
        _coordinateReference = coordinateReference;
    }

    /// <summary>The backing coordinate sequence.</summary>
    public ICoordinateSequence Sequence => _sequence;

    /// <summary>Coordinate at <paramref name="index"/>.</summary>
    public Coordinate this[int index] => _sequence.GetCoordinate(index);

    public GeometryType Type => GeometryType.LineString;

    public CoordinateLayout Layout => _sequence.Layout;

    public CoordinateReference? CoordinateReference => _coordinateReference;

    public bool IsEmpty => _sequence.Count == 0;

    public int CoordinateCount => _sequence.Count;

    public Envelope? Envelope => _sequence.Count == 0 ? null : Spatial.Core.Geometry.Envelope.FromSequence(_sequence);

    public bool Equals(LineString? other) => other is not null && ContentEquals(other);

    public override bool Equals(object? obj) => obj is LineString other && ContentEquals(other);

    private bool ContentEquals(LineString other) =>
        Nullable.Equals(_coordinateReference, other._coordinateReference)
        && CoordinateSequenceComparer.ValuesEqual(_sequence, other._sequence);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_coordinateReference);
        hash.Add(CoordinateSequenceComparer.GetHashCode(_sequence));
        return hash.ToHashCode();
    }

    public override string ToString() => $"LineString [{_sequence.Count} coordinates]";
}
