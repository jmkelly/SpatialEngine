
namespace Spatial.Core.Geometry;

/// <summary>
/// Coordinate sequence backed by a single packed <see cref="double"/> array,
/// ordered by coordinate and then by layout ordinate. One heap object for the
/// whole sequence — never one per coordinate.
/// </summary>
public readonly struct PackedCoordinateSequence : ICoordinateSequence, IEquatable<PackedCoordinateSequence>
{
    private readonly double[] _values;
    private readonly CoordinateLayout _layout;
    private readonly int _count;

    /// <summary>
    /// Wraps a copy of <paramref name="values"/> laid out as
    /// <paramref name="layout"/> ordinates per coordinate.
    /// </summary>
    public PackedCoordinateSequence(ReadOnlySpan<double> values, CoordinateLayout layout)
        : this(values.ToArray(), layout)
    {
    }

    /// <summary>Wraps <paramref name="values"/> directly, taking ownership (no copy).</summary>
    internal PackedCoordinateSequence(double[] values, CoordinateLayout layout)
    {
        var stride = layout.OrdinateCount();
        if (values.Length % stride != 0)
        {
            throw new ArgumentException(
                $"Packed sequence length {values.Length} is not a multiple of stride {stride} for layout {layout}.", nameof(values));
        }

        _values = values;
        _layout = layout;
        _count = values.Length / stride;
    }

    /// <summary>
    /// Packs the coordinates, inferring the layout from the ordinates present
    /// (or using <paramref name="layout"/> when given). A non-null ordinate
    /// that the layout does not store is rejected — no silent data loss.
    /// </summary>
    public static PackedCoordinateSequence FromCoordinates(ReadOnlySpan<Coordinate> coordinates, CoordinateLayout? layout = null)
    {
        var resolved = layout ?? coordinates.Infer();
        var stride = resolved.OrdinateCount();
        var hasZ = resolved.HasZ();
        var hasM = resolved.HasM();
        var values = new double[coordinates.Length * stride];

        for (var i = 0; i < coordinates.Length; i++)
        {
            var coordinate = coordinates[i];
            var offset = i * stride;
            values[offset] = coordinate.X;
            values[offset + 1] = coordinate.Y;
            var ordinateOffset = 2;
            if (hasZ)
            {
                values[offset + ordinateOffset++] = PackOrdinate(coordinate.Z, stored: true, "Z", i, resolved);
            }
            else
            {
                PackOrdinate(coordinate.Z, stored: false, "Z", i, resolved);
            }

            if (hasM)
            {
                // In Xym the M ordinate lives at offset 2; in Xyzm it follows Z at offset 3.
                values[offset + ordinateOffset] = PackOrdinate(coordinate.M, stored: true, "M", i, resolved);
            }
            else
            {
                PackOrdinate(coordinate.M, stored: false, "M", i, resolved);
            }
        }

        return new PackedCoordinateSequence(values, resolved);
    }

    /// <inheritdoc />
    public int Count => _count;

    /// <inheritdoc />
    public CoordinateLayout Layout => _layout;

    /// <inheritdoc />
    public double GetOrdinate(int index, Ordinate ordinate)
    {
        if ((uint)index >= (uint)_count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Index {index} is out of range for a sequence of {_count} coordinates.");
        }

        var stride = _layout.OrdinateCount();
        var offset = ordinate switch
        {
            Ordinate.X => 0,
            Ordinate.Y => 1,
            Ordinate.Z when _layout.HasZ() => 2,
            // In Xym the M ordinate lives at offset 2; in Xyzm it follows Z at offset 3.
            Ordinate.M when _layout.HasM() => _layout.HasZ() ? 3 : 2,
            Ordinate.Z => throw OrdinateNotStored(ordinate, _layout),
            Ordinate.M => throw OrdinateNotStored(ordinate, _layout),
            _ => throw UnknownOrdinate(ordinate),
        };

        return _values[(index * stride) + offset];
    }

    /// <summary>Packed ordinate values (debugging and interop).</summary>
    internal ReadOnlySpan<double> Values => _values ?? [];

    public bool Equals(PackedCoordinateSequence other) => CoordinateSequenceComparer.Equals(this, other);

    public override bool Equals(object? obj) => obj is PackedCoordinateSequence other && Equals(other);

    public override int GetHashCode() => CoordinateSequenceComparer.GetHashCode(this);

    public static bool operator ==(PackedCoordinateSequence left, PackedCoordinateSequence right) => left.Equals(right);

    public static bool operator !=(PackedCoordinateSequence left, PackedCoordinateSequence right) => !left.Equals(right);

    public override string ToString() => $"PackedCoordinateSequence [{_count} × {_layout}]";

    private static double PackOrdinate(double? value, bool stored, string name, int index, CoordinateLayout layout)
    {
        if (value is null)
        {
            return stored ? double.NaN : 0;
        }

        if (!stored)
        {
            throw new ArgumentException(
                FormattableString.Invariant($"Coordinate at index {index} has {name}={value} but layout {layout} does not store {name}; choose a layout that includes {name} or strip the ordinate explicitly."));
        }

        return value.Value;
    }

    private static ArgumentOutOfRangeException OrdinateNotStored(Ordinate ordinate, CoordinateLayout layout) =>
        new(nameof(ordinate), ordinate, $"Layout {layout} does not store {ordinate}.");

    private static ArgumentOutOfRangeException UnknownOrdinate(Ordinate ordinate) =>
        new(nameof(ordinate), ordinate, $"Unknown ordinate '{ordinate}'.");
}
