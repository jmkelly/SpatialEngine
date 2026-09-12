namespace Spatial.Core.Geometry;

/// <summary>
/// Which ordinates a coordinate sequence stores. A layout is a structural
/// property: consumers can inspect it without knowing the storage backing.
/// </summary>
public enum CoordinateLayout : byte
{
    /// <summary>X and Y only.</summary>
    Xy = 0,

    /// <summary>X, Y and Z.</summary>
    Xyz = 1,

    /// <summary>X, Y and M.</summary>
    Xym = 2,

    /// <summary>X, Y, Z and M.</summary>
    Xyzm = 3,
}

/// <summary>Structural helpers for <see cref="CoordinateLayout"/>.</summary>
public static class CoordinateLayoutExtensions
{
    /// <summary>Number of doubles one coordinate occupies in this layout.</summary>
    public static int OrdinateCount(this CoordinateLayout layout) => layout switch
    {
        CoordinateLayout.Xy => 2,
        CoordinateLayout.Xyz or CoordinateLayout.Xym => 3,
        CoordinateLayout.Xyzm => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, $"Unknown coordinate layout '{layout}'."),
    };

    /// <summary>Whether this layout stores a Z ordinate.</summary>
    public static bool HasZ(this CoordinateLayout layout) =>
        layout is CoordinateLayout.Xyz or CoordinateLayout.Xyzm;

    /// <summary>Whether this layout stores an M ordinate.</summary>
    public static bool HasM(this CoordinateLayout layout) =>
        layout is CoordinateLayout.Xym or CoordinateLayout.Xyzm;

    /// <summary>The layout carrying exactly the requested ordinates.</summary>
    public static CoordinateLayout FromOrdinates(bool hasZ, bool hasM) => (hasZ, hasM) switch
    {
        (true, true) => CoordinateLayout.Xyzm,
        (true, false) => CoordinateLayout.Xyz,
        (false, true) => CoordinateLayout.Xym,
        (false, false) => CoordinateLayout.Xy,
    };

    /// <summary>
    /// Infers the layout that can represent every ordinate present in
    /// <paramref name="coordinates"/>: the presence of any Z ordinate raises
    /// the layout to Xyz/Xyzm, the presence of any M ordinate to Xym/Xyzm.
    /// Inference never drops a present ordinate.
    /// </summary>
    public static CoordinateLayout Infer(this ReadOnlySpan<Coordinate> coordinates)
    {
        var hasZ = false;
        var hasM = false;
        foreach (var coordinate in coordinates)
        {
            hasZ |= coordinate.Z is not null;
            hasM |= coordinate.M is not null;
        }

        return FromOrdinates(hasZ, hasM);
    }
}
