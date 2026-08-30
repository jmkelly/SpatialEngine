namespace Spatial.Core.Geometry;

/// <summary>
/// Ordered coordinates of a geometry part. Read-only. Implementations choose
/// the storage backing (packed doubles, coordinate arrays, and later shared
/// memory or Arrow); consumers see only counts, layout and ordinates.
/// </summary>
public interface ICoordinateSequence
{
    /// <summary>Number of coordinates.</summary>
    int Count { get; }

    /// <summary>Which ordinates every coordinate carries.</summary>
    CoordinateLayout Layout { get; }

    /// <summary>
    /// Gets one ordinate of the coordinate at <paramref name="index"/>.
    /// Throws <see cref="ArgumentOutOfRangeException"/> when the index is out
    /// of range or the ordinate is not part of <see cref="Layout"/>.
    /// </summary>
    double GetOrdinate(int index, Ordinate ordinate);

    /// <summary>
    /// Returns the coordinate at <paramref name="index"/>. Ordinates that are
    /// part of <see cref="Layout"/> but have no stored value (for example a
    /// null Z in an Xyz sequence) surface as <see cref="double.NaN"/>; an
    /// absent ordinate surfaces as <c>null</c>.
    /// </summary>
    Coordinate GetCoordinate(int index) => new(
        GetOrdinate(index, Ordinate.X),
        GetOrdinate(index, Ordinate.Y),
        Layout.HasZ() ? GetOrdinate(index, Ordinate.Z) : null,
        Layout.HasM() ? GetOrdinate(index, Ordinate.M) : null);
}

/// <summary>Convenience accessors over <see cref="ICoordinateSequence"/>.</summary>
public static class CoordinateSequenceExtensions
{
    public static double GetX(this ICoordinateSequence sequence, int index) => sequence.GetOrdinate(index, Ordinate.X);

    public static double GetY(this ICoordinateSequence sequence, int index) => sequence.GetOrdinate(index, Ordinate.Y);

    public static double GetZ(this ICoordinateSequence sequence, int index) => sequence.GetOrdinate(index, Ordinate.Z);

    public static double GetM(this ICoordinateSequence sequence, int index) => sequence.GetOrdinate(index, Ordinate.M);

    /// <summary>Streams the sequence as coordinates.</summary>
    public static IEnumerable<Coordinate> AsEnumerable(this ICoordinateSequence sequence)
    {
        for (var i = 0; i < sequence.Count; i++)
        {
            yield return sequence.GetCoordinate(i);
        }
    }
}

/// <summary>
/// Ordinate-wise sequence comparison. Two sequences are equal when their
/// counts, layouts and ordinate values match, regardless of storage backing,
/// which keeps packed and array sequences interchangeable.
/// </summary>
public static class CoordinateSequenceComparer
{
    public static bool Equals(ICoordinateSequence? left, ICoordinateSequence? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return ValuesEqual(left, right);
    }

    /// <summary>
    /// Compares counts, layouts and every ordinate value. Ordinate equality
    /// uses <see cref="double.Equals(double)"/> so NaN values compare equal.
    /// </summary>
    public static bool ValuesEqual(ICoordinateSequence left, ICoordinateSequence right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Count != right.Count || left.Layout != right.Layout)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!CoordinatesEqual(left, right, i))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CoordinatesEqual(ICoordinateSequence left, ICoordinateSequence right, int index)
    {
        var hasZ = left.Layout.HasZ();
        var hasM = left.Layout.HasM();
        return left.GetOrdinate(index, Ordinate.X).Equals(right.GetOrdinate(index, Ordinate.X))
            && left.GetOrdinate(index, Ordinate.Y).Equals(right.GetOrdinate(index, Ordinate.Y))
            && (!hasZ || left.GetOrdinate(index, Ordinate.Z).Equals(right.GetOrdinate(index, Ordinate.Z)))
            && (!hasM || left.GetOrdinate(index, Ordinate.M).Equals(right.GetOrdinate(index, Ordinate.M)));
    }

    /// <summary>Hashes layout and every ordinate value, consistent with <see cref="ValuesEqual"/>.</summary>
    public static int GetHashCode(ICoordinateSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);

        var hash = new HashCode();
        hash.Add(sequence.Layout);
        var hasZ = sequence.Layout.HasZ();
        var hasM = sequence.Layout.HasM();
        for (var i = 0; i < sequence.Count; i++)
        {
            hash.Add(sequence.GetOrdinate(i, Ordinate.X));
            hash.Add(sequence.GetOrdinate(i, Ordinate.Y));
            if (hasZ)
            {
                hash.Add(sequence.GetOrdinate(i, Ordinate.Z));
            }

            if (hasM)
            {
                hash.Add(sequence.GetOrdinate(i, Ordinate.M));
            }
        }

        return hash.ToHashCode();
    }
}
