using Spatial.Contracts;
using ProjCs = ProjNet.CoordinateSystems;
using ProjTf = ProjNet.CoordinateSystems.Transformations;

namespace Spatial.Transformations.ProjNet.Grids;

/// <summary>
/// The transform the verb runs when a datum shift grid is deployed (ADR-0107),
/// as four steps with a choice in the middle two.
/// <para>
/// The composition is <em>source → the source's own geographic coordinates →
/// datum shift → WGS 84 → the target's own geographic coordinates → target</em>,
/// and the two outer steps are pure projection maths. ProjNet cannot be asked
/// for that composition directly, because it applies a datum's shift wherever a
/// pair of coordinate systems disagree about a datum: a leg taken between the
/// catalogued systems re-applies the shift an earlier leg has already applied.
/// So the outer steps are built on <em>shift-free</em> copies of the source
/// and target systems, in which the datum's WGS 84 conversion is zeroed and
/// only the ellipsoid, the projection and the axes remain. There is then no
/// shift anywhere in an outer leg for ProjNet to apply, and the datum shift is
/// entirely this type's.
/// </para>
/// <para>
/// The middle two steps are where the choice lives, and it is per
/// <em>coordinate</em> rather than per request, because a grid is a fact about
/// the ground and a geometry is not obliged to stay inside one block. A point
/// the grid covers is shifted by the grid; a point it does not cover falls back
/// to the classic Helmert for that same point. Each leg is taken with exactly
/// one end carrying the shift — the catalogued system on that datum's own side
/// and the shift-free pivot on the other — which is the pairing ProjNet
/// evaluates as a single datum shift, in the direction asked for.
/// </para>
/// </summary>
internal sealed class GridShiftPlan
{
    private static readonly ProjTf.CoordinateTransformationFactory Transformations = new();

    /// <summary>The EPSG code of the pivot every datum in the catalogue is published against.</summary>
    private const int WorldDatumCode = 4326;

    private readonly ProjTf.MathTransform _toSourceGeographic;

    private readonly DatumShift _fromShift;

    private readonly DatumShift _toShift;

    /// <summary>The target's own projection, from its geographic coordinates to its eastings and northings.</summary>
    private readonly ProjTf.MathTransform _toTarget;

    private GridShiftPlan(
        ProjTf.MathTransform toSourceGeographic,
        DatumShift fromShift,
        DatumShift toShift,
        ProjTf.MathTransform toTarget)
    {
        _toSourceGeographic = toSourceGeographic;
        _fromShift = fromShift;
        _toShift = toShift;
        _toTarget = toTarget;
    }

    /// <summary>
    /// The plan for a source-to-target pair, or null when neither datum is
    /// served by a deployed grid — in which case the caller keeps the single
    /// ProjNet composition it has always used, byte for byte.
    /// </summary>
    public static GridShiftPlan? For(
        int sourceCode,
        int targetCode,
        DatumNode from,
        DatumNode to,
        DatumShiftGridRegistry registry)
    {
        var fromGrid = registry.TryGet(from.Code, out var forwardGrid) ? forwardGrid : null;
        var toGrid = registry.TryGet(to.Code, out var backwardGrid) ? backwardGrid : null;
        if (fromGrid is null && toGrid is null)
        {
            return null;
        }

        // The pivot as the two middle legs see it: WGS 84 with its (zero)
        // conversion still on, so pairing a catalogued system with it is a
        // single datum shift in the direction asked for.
        var pivot = Required(ProjEpsgCatalog.ShiftFreeGeographicBaseOf(WorldDatumCode), WorldDatumCode);
        var sourceGeographic = Required(ProjEpsgCatalog.ShiftFreeGeographicBaseOf(sourceCode), sourceCode);
        var targetGeographic = Required(ProjEpsgCatalog.ShiftFreeGeographicBaseOf(targetCode), targetCode);
        var sourceReal = ProjEpsgCatalog.GeographicBaseOf(sourceCode);
        var targetReal = ProjEpsgCatalog.GeographicBaseOf(targetCode);

        return new GridShiftPlan(
            Transformations.CreateFromCoordinateSystems(
                Required(ProjEpsgCatalog.ShiftFreeOf(sourceCode), sourceCode), sourceGeographic).MathTransform,
            new DatumShift(fromGrid, Helmert(sourceReal, pivot)),
            new DatumShift(toGrid, Helmert(pivot, targetReal)),
            Transformations.CreateFromCoordinateSystems(
                targetGeographic, Required(ProjEpsgCatalog.ShiftFreeOf(targetCode), targetCode)).MathTransform);
    }

    /// <summary>
    /// A ProjNet leg pairing a system that carries its datum's shift with a
    /// shift-free one, in the direction asked for. Exactly one end carries the
    /// shift, which is the pairing ProjNet evaluates as a single datum shift;
    /// null when the datum is the pivot itself, because a pivot has no shift
    /// to apply, and null when the catalogue builds no system for the end,
    /// because a leg with a missing end is not a leg.
    /// </summary>
    private static ProjTf.MathTransform? Helmert(ProjCs.CoordinateSystem? shifted, ProjCs.CoordinateSystem? shiftFree)
    {
        if (shifted is null || shiftFree is null || shifted.Equals(shiftFree))
        {
            return null;
        }

        return Transformations.CreateFromCoordinateSystems(shifted, shiftFree).MathTransform;
    }

    private static ProjCs.CoordinateSystem Required(ProjCs.CoordinateSystem? system, int code) =>
        // A structured failure, not an assertion: a catalogue that serves a
        // code the plan cannot build a shift-free copy of is a request this
        // host cannot answer, and the verb's failures are SpatialException
        // codes (invalid.arguments) whatever the cause.
        system ?? throw SpatialException.BadArguments(
            $"EPSG:{code} is in the catalogue but no datum shift grid can be applied to it.");

    /// <summary>
    /// Shifts one coordinate, choosing the grid or the Helmert for that
    /// coordinate rather than for the request. The choice is the point: a
    /// geometry crossing the edge of a grid's block is shifted by the grid
    /// where the grid reaches and by the Helmert where it does not, in one
    /// request, and nothing is ever extrapolated across that edge.
    /// </summary>
    public (double X, double Y) Shift(double x, double y)
    {
        var (longitude, latitude) = _toSourceGeographic.Transform(x, y);
        (longitude, latitude) = _fromShift.ApplyForward(longitude, latitude);
        (longitude, latitude) = _toShift.ApplyInverse(longitude, latitude);
        return _toTarget.Transform(longitude, latitude);
    }

    /// <summary>
    /// One side of the pivot: the grid deployed for that datum and the classic
    /// Helmert standing behind it. Either may be absent — a datum the
    /// catalogue publishes no grid for has only the Helmert, and the pivot has
    /// neither.
    /// </summary>
    private sealed record DatumShift(DatumShiftGrid? Grid, ProjTf.MathTransform? Helmert)
    {
        /// <summary>
        /// The shift from the datum to the pivot. The grid answers only where
        /// it has a cell to interpolate from; everywhere else the Helmert
        /// stands, because the alternative is refusing a point the engine can
        /// place to metres.
        /// </summary>
        public (double Longitude, double Latitude) ApplyForward(double longitude, double latitude)
        {
            if (Grid is not null && Grid.TryShiftForward(longitude, latitude, out var latitudeShift, out var longitudeShift))
            {
                return (longitudeShift, latitudeShift);
            }

            return Helmert is null ? (longitude, latitude) : Shifted(Helmert, longitude, latitude);
        }

        /// <summary>The same shift the other way, for a target datum's leg.</summary>
        public (double Longitude, double Latitude) ApplyInverse(double longitude, double latitude)
        {
            if (Grid is not null && Grid.TryShiftInverse(longitude, latitude, out var latitudeShift, out var longitudeShift))
            {
                return (longitudeShift, latitudeShift);
            }

            return Helmert is null ? (longitude, latitude) : Shifted(Helmert, longitude, latitude);
        }

        /// <summary>
        /// ProjNet hands back an (x, y) pair; a datum shift is a longitude and a
        /// latitude, and reading them the other way round is a transposition
        /// that stays plausible at a glance. The transform is called once.
        /// </summary>
        private static (double Longitude, double Latitude) Shifted(ProjTf.MathTransform helmert, double longitude, double latitude)
        {
            var (x, y) = helmert.Transform(longitude, latitude);
            return (x, y);
        }
    }
}
