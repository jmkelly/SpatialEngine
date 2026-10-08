using Spatial.Core.Geometry;

namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// The coordinate layout a PostGIS geometry column <em>declares</em>
/// (ADR-0084). PostgreSQL reports a column's type modifier as the formatted
/// type PostGIS renders for it — <c>geometry(Point,4326)</c>,
/// <c>geometry(PointZ,4326)</c>, <c>geometry(PointZM,4326)</c> — and the
/// declared modifier is a type-system proof: a column typed
/// <c>geometry(PointZ,4326)</c> cannot hold a coordinate without a Z.
/// <para/>
/// A column declared plain <c>geometry</c> (no modifier) constrains nothing,
/// so it proves nothing either and is reported <see cref="CoordinateLayout.Xy"/>.
/// That is a deliberate under-report: a column may still hold Z values the
/// engine will faithfully serve, but the schema is not evidence, and the
/// GeoServices layer metadata must not claim what the schema cannot back
/// (ADR-0092). Anything unrecognised is Xy for the same reason — the parse
/// is a proof, never a guess.
/// </summary>
internal static class PostgisCoordinateLayout
{
    /// <summary>
    /// The layout the declared type modifier proves, or
    /// <see cref="CoordinateLayout.Xy"/> when it declares none (or is not a
    /// PostGIS geometry modifier at all).
    /// </summary>
    public static CoordinateLayout FromTypeModifier(string? typeModifier)
    {
        if (string.IsNullOrWhiteSpace(typeModifier))
        {
            return CoordinateLayout.Xy;
        }

        return LayoutOfDeclared(DeclaredName(typeModifier));
    }

    /// <summary>
    /// The declared geometry name inside the modifier's parentheses
    /// (<c>geometry(PointZ,4326)</c> declares <c>PointZ</c>), or <c>null</c>
    /// when the modifier declares no parenthesised type at all.
    /// </summary>
    private static string? DeclaredName(string typeModifier)
    {
        var open = typeModifier.IndexOf('(');
        if (open < 0)
        {
            return null;
        }

        var close = typeModifier.IndexOf(')', open);
        var declared = typeModifier[(open + 1)..(close < 0 ? typeModifier.Length : close)];
        return declared.Split(',')[0].Trim();
    }

    /// <summary>The layout a declared name proves, or Xy when it declares none.</summary>
    private static CoordinateLayout LayoutOfDeclared(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return CoordinateLayout.Xy;
        }

        return LayoutOfSuffix(name);
    }

    /// <summary>The layout a declared name's ordinate suffix proves (<c>ZM</c>, <c>Z</c>, <c>M</c>, or none).</summary>
    private static CoordinateLayout LayoutOfSuffix(string name) =>
        name.EndsWith("ZM", StringComparison.OrdinalIgnoreCase)
            ? CoordinateLayout.Xyzm
            : name.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
                ? CoordinateLayout.Xyz
                : name.EndsWith("M", StringComparison.OrdinalIgnoreCase)
                    ? CoordinateLayout.Xym
                    : CoordinateLayout.Xy;
}
