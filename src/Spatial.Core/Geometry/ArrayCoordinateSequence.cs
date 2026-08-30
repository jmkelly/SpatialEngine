namespace Spatial.Core.Geometry;

/// <summary>
/// Coordinate sequence backed by a <see cref="Coordinate"/> array. Intended
/// for small, construction-oriented uses and as the reference implementation;
/// prefer <see cref="PackedCoordinateSequence"/> for large sequences.
///
/// Stored coordinates are normalised to the layout: an ordinate the layout
/// stores but the coordinate leaves null is stored as <see cref="double.NaN"/>;
/// a non-null ordinate the layout does not store is rejected.
/// </summary>
public readonly struct ArrayCoordinateSequence : ICoordinateSequence, IEquatable<ArrayCoordinateSequence>
{
    private readonly Coordinate[] _coordinates;
    private readonly CoordinateLayout _layout;

    /// <summary>
    /// Wraps a copy of <paramref name="coordinates"/>, inferring the layout
    /// from the ordinates present (or using <paramref name="layout"/> when
    /// given).
    /// </summary>
    public ArrayCoordinateSequence(ReadOnlySpan<Coordinate> coordinates, CoordinateLayout? layout = null)
    {
        var resolved = layout ?? coordinates.Infer();
        var hasZ = resolved.HasZ();
        var hasM = resolved.HasM();
        var stored = new Coordinate[coordinates.Length];

        for (var i = 0; i < coordinates.Length; i++)
        {
            var coordinate = coordinates[i];
            if (coordinate.Z is null && hasZ)
            {
                coordinate = coordinate with { Z = double.NaN };
            }
            else if (coordinate.Z is not null && !hasZ)
            {
                ThrowOrdinateNotStored(i, coordinate.Z, "Z", resolved);
            }

            if (coordinate.M is null && hasM)
            {
                coordinate = coordinate with { M = double.NaN };
            }
            else if (coordinate.M is not null && !hasM)
            {
                ThrowOrdinateNotStored(i, coordinate.M, "M", resolved);
            }

            stored[i] = coordinate;
        }

        _coordinates = stored;
        _layout = resolved;
    }

    /// <inheritdoc />
    public int Count => _coordinates.Length;

    /// <inheritdoc />
    public CoordinateLayout Layout => _layout;

    /// <inheritdoc />
    public double GetOrdinate(int index, Ordinate ordinate)
    {
        if ((uint)index >= (uint)_coordinates.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Index {index} is out of range for a sequence of {_coordinates.Length} coordinates.");
        }

        var coordinate = _coordinates[index];
        return ordinate switch
        {
            Ordinate.X => coordinate.X,
            Ordinate.Y => coordinate.Y,
            Ordinate.Z when _layout.HasZ() => coordinate.Z ?? double.NaN,
            Ordinate.M when _layout.HasM() => coordinate.M ?? double.NaN,
            Ordinate.Z => throw OrdinateNotStored(ordinate, _layout),
            Ordinate.M => throw OrdinateNotStored(ordinate, _layout),
            _ => throw UnknownOrdinate(ordinate),
        };
    }

    public bool Equals(ArrayCoordinateSequence other) => CoordinateSequenceComparer.Equals(this, other);

    public override bool Equals(object? obj) => obj is ArrayCoordinateSequence other && Equals(other);

    public override int GetHashCode() => CoordinateSequenceComparer.GetHashCode(this);

    public static bool operator ==(ArrayCoordinateSequence left, ArrayCoordinateSequence right) => left.Equals(right);

    public static bool operator !=(ArrayCoordinateSequence left, ArrayCoordinateSequence right) => !left.Equals(right);

    public override string ToString() => $"ArrayCoordinateSequence [{_coordinates.Length} × {_layout}]";

    private static void ThrowOrdinateNotStored(int index, double? value, string name, CoordinateLayout layout) =>
        throw new ArgumentException(
            FormattableString.Invariant($"Coordinate at index {index} has {name}={value} but layout {layout} does not store {name}; choose a layout that includes {name} or strip the ordinate explicitly."));

    private static ArgumentOutOfRangeException OrdinateNotStored(Ordinate ordinate, CoordinateLayout layout) =>
        new(nameof(ordinate), ordinate, $"Layout {layout} does not store {ordinate}.");

    private static ArgumentOutOfRangeException UnknownOrdinate(Ordinate ordinate) =>
        new(nameof(ordinate), ordinate, $"Unknown ordinate '{ordinate}'.");
}
