using Spatial.Contracts.Transformations;
using Spatial.Transformations.ProjNet;
using ProjCs = global::ProjNet.CoordinateSystems;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Direct tests of the internal CRS description mapper (ADR-0027): the
/// exotic coordinate-system families and axis orientations the embedded
/// catalogue does not serve (geocentric, vertical, compound, fitted; west,
/// south, up, down, other) are constructed through ProjNet's factory and
/// mapped to the contract vocabulary, so the family/axis classification
/// behaviour is fully exercised.
/// </summary>
public sealed class ProjNetCrsMapperTests
{
    private static readonly ProjCs.CoordinateSystemFactory Factory = new();
    private static readonly ProjCs.Ellipsoid Wgs84 =
        Factory.CreateFlattenedSphere("WGS 84", 6378137, 298.257223563, ProjCs.LinearUnit.Metre);
    private static readonly ProjCs.HorizontalDatum Wgs84Datum =
        Factory.CreateHorizontalDatum("World Geodetic System 1984", ProjCs.DatumType.HD_Geocentric, Wgs84, new ProjCs.Wgs84ConversionInfo(0, 0, 0, 0, 0, 0, 0));

    [Fact]
    public void Every_catalogue_entry_is_classified_as_geographic_or_projected()
    {
        foreach (var code in ProjEpsgCatalog.Codes)
        {
            ProjEpsgCatalog.TryGet(code, out var system);
            var description = ProjNetCrsMapper.Describe(code, system!);
            Assert.True(
                description.Kind is CrsKind.Geographic or CrsKind.Projected,
                $"code {code} classified as {description.Kind}.");
        }
    }

    [Fact]
    public void A_geocentric_system_is_classified_geocentric()
    {
        var system = Factory.CreateGeocentricCoordinateSystem(
            "Geocentric", Wgs84Datum, ProjCs.LinearUnit.Metre,
            Factory.CreatePrimeMeridian("Greenwich", ProjCs.AngularUnit.Degrees, 0));

        var description = ProjNetCrsMapper.Describe(0, system);
        Assert.Equal(CrsKind.Geocentric, description.Kind);
        Assert.Equal("Geocentric", description.Name);
        Assert.Equal("World Geodetic System 1984", description.Datum);
    }

    [Fact]
    public void A_vertical_system_is_classified_vertical()
    {
        var datum = Factory.CreateVerticalDatum("Ordnance Datum Newlyn", ProjCs.DatumType.VD_Orthometric);
        var system = Factory.CreateVerticalCoordinateSystem(
            "ODN height", datum, ProjCs.LinearUnit.Metre,
            new ProjCs.AxisInfo("Up", ProjCs.AxisOrientationEnum.Up));

        var description = ProjNetCrsMapper.Describe(0, system);
        Assert.Equal(CrsKind.Vertical, description.Kind);
        Assert.Equal(AxisOrientation.Up, description.Axes[0].Orientation);
    }

    [Fact]
    public void A_compound_system_is_classified_compound()
    {
        var geographic = Factory.CreateGeographicCoordinateSystem(
            "WGS 84", ProjCs.AngularUnit.Degrees, Wgs84Datum,
            Factory.CreatePrimeMeridian("Greenwich", ProjCs.AngularUnit.Degrees, 0),
            new ProjCs.AxisInfo("Lon", ProjCs.AxisOrientationEnum.East),
            new ProjCs.AxisInfo("Lat", ProjCs.AxisOrientationEnum.North));
        var vertical = Factory.CreateVerticalCoordinateSystem(
            "height", Factory.CreateVerticalDatum("datum", ProjCs.DatumType.VD_Ellipsoidal),
            ProjCs.LinearUnit.Metre, new ProjCs.AxisInfo("Up", ProjCs.AxisOrientationEnum.Up));

        var description = ProjNetCrsMapper.Describe(0, Factory.CreateCompoundCoordinateSystem("Compound", geographic, vertical));
        Assert.Equal(CrsKind.Compound, description.Kind);
        Assert.Equal(3, description.Dimension);
        Assert.Equal("Lon", description.Axes[0].Name);
        Assert.Equal("Up", description.Axes[2].Name);
    }

    [Fact]
    public void A_fitted_system_is_classified_other()
    {
        var baseSystem = Factory.CreateGeographicCoordinateSystem(
            "WGS 84", ProjCs.AngularUnit.Degrees, Wgs84Datum,
            Factory.CreatePrimeMeridian("Greenwich", ProjCs.AngularUnit.Degrees, 0),
            new ProjCs.AxisInfo("Lon", ProjCs.AxisOrientationEnum.East),
            new ProjCs.AxisInfo("Lat", ProjCs.AxisOrientationEnum.North));
        var system = Factory.CreateFittedCoordinateSystem(
            "Fitted", baseSystem, new ProjCs.Transformations.AffineTransform(1, 0, 0, 0, 1, 0), [new ProjCs.AxisInfo("e", ProjCs.AxisOrientationEnum.Other)]);

        var description = ProjNetCrsMapper.Describe(0, system);
        Assert.Equal(CrsKind.Other, description.Kind);
    }

    [Fact]
    public void Geographic_axes_with_the_other_orientations_are_mapped_through()
    {
        var system = Factory.CreateGeographicCoordinateSystem(
            "Swapped", ProjCs.AngularUnit.Degrees, Wgs84Datum,
            Factory.CreatePrimeMeridian("Greenwich", ProjCs.AngularUnit.Degrees, 0),
            new ProjCs.AxisInfo("Lon", ProjCs.AxisOrientationEnum.West),
            new ProjCs.AxisInfo("Lat", ProjCs.AxisOrientationEnum.South));

        var description = ProjNetCrsMapper.Describe(0, system);
        Assert.Equal(AxisOrientation.West, description.Axes[0].Orientation);
        Assert.Equal(AxisOrientation.South, description.Axes[1].Orientation);
    }

    [Fact]
    public void A_down_axis_is_mapped_through()
    {
        var system = Factory.CreateVerticalCoordinateSystem(
            "down", Factory.CreateVerticalDatum("datum", ProjCs.DatumType.VD_Depth),
            ProjCs.LinearUnit.Metre, new ProjCs.AxisInfo("Down", ProjCs.AxisOrientationEnum.Down));

        var description = ProjNetCrsMapper.Describe(0, system);
        Assert.Equal(AxisOrientation.Down, description.Axes[0].Orientation);
    }
}
