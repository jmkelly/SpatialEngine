using System.Globalization;
using Spatial.PluginSdk.Transformations;
using ProjCs = ProjNet.CoordinateSystems;

namespace Spatial.Transformations.ProjNet;

/// <summary>
/// Maps ProjNet's coordinate-system model to the contract's
/// <see cref="CrsDescription"/> (ADR-0027): the CRS name, family
/// (geographic / projected / …), axes with units and orientations, datum and
/// ellipsoid. No core geometry types are involved — descriptions are pure
/// metadata. The axis order reported is the CRS's declared order; the
/// engine's geometry convention is always x-first (x = the first axis:
/// longitude for geographic, easting for projected), which is exactly the
/// order ProjNet 2.1's math transforms consume and produce.
/// </summary>
internal static class ProjNetCrsMapper
{
    public static CrsDescription Describe(int code, ProjCs.CoordinateSystem coordinateSystem)
    {
        var axes = new CrsAxis[coordinateSystem.Dimension];
        for (var index = 0; index < axes.Length; index++)
        {
            axes[index] = AxisOf(coordinateSystem, index);
        }

        // ProjNet 2.1's projected CRSs expose the horizontal datum through
        // their geographic coordinate system, not directly (their own
        // HorizontalDatum stays null) - read the datum from wherever the
        // family carries it.
        var datum = coordinateSystem switch
        {
            ProjCs.ProjectedCoordinateSystem projected => projected.GeographicCoordinateSystem.HorizontalDatum,
            ProjCs.HorizontalCoordinateSystem horizontal => horizontal.HorizontalDatum,
            _ => null,
        };
        var ellipsoid = datum?.Ellipsoid is { } ellipsoidDefinition
            ? new CrsEllipsoid(
                ellipsoidDefinition.Name,
                ellipsoidDefinition.SemiMajorAxis,
                ellipsoidDefinition.SemiMinorAxis,
                UnitName(ellipsoidDefinition.AxisUnit))
            : null;

        return new CrsDescription(
            "EPSG",
            code.ToString(CultureInfo.InvariantCulture),
            coordinateSystem.Name,
            KindOf(coordinateSystem),
            coordinateSystem.Dimension,
            axes,
            datum?.Name,
            ellipsoid);
    }

    private static CrsKind KindOf(ProjCs.CoordinateSystem coordinateSystem) => coordinateSystem switch
    {
        ProjCs.GeocentricCoordinateSystem => CrsKind.Geocentric,
        ProjCs.GeographicCoordinateSystem => CrsKind.Geographic,
        ProjCs.ProjectedCoordinateSystem => CrsKind.Projected,
        ProjCs.VerticalCoordinateSystem => CrsKind.Vertical,
        ProjCs.CompoundCoordinateSystem => CrsKind.Compound,
        _ => CrsKind.Other,
    };

    private static CrsAxis AxisOf(ProjCs.CoordinateSystem coordinateSystem, int index)
    {
        var axis = coordinateSystem.GetAxis(index);
        return new CrsAxis(axis.Name, OrientationOf(axis.Orientation), UnitName(coordinateSystem.GetUnits(index)));
    }

    private static AxisOrientation OrientationOf(ProjCs.AxisOrientationEnum orientation) => orientation switch
    {
        ProjCs.AxisOrientationEnum.East => AxisOrientation.East,
        ProjCs.AxisOrientationEnum.North => AxisOrientation.North,
        ProjCs.AxisOrientationEnum.West => AxisOrientation.West,
        ProjCs.AxisOrientationEnum.South => AxisOrientation.South,
        ProjCs.AxisOrientationEnum.Up => AxisOrientation.Up,
        ProjCs.AxisOrientationEnum.Down => AxisOrientation.Down,
        _ => AxisOrientation.Other,
    };

    private static string UnitName(ProjCs.IUnit? unit) => string.IsNullOrWhiteSpace(unit?.Name) ? "unknown" : unit.Name!;
}
