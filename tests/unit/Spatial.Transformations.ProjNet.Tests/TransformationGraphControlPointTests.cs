using Spatial.Contracts.TransformationSearch;
using Spatial.Core.Geometry;
using static Spatial.Transformations.ProjNet.Tests.GraphInvoker;
using static Spatial.Transformations.ProjNet.Tests.TransformInvoker;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The published parameters must be the transform the engine performs
/// (ADR-0087). The check is end to end and independent of the code that
/// produced them: take the Helmert the graph publishes for WGS 84 to OSGB 36,
/// apply it to the London control point by hand — geodetic to geocentric on
/// the WGS 84 ellipsoid, the shift, geocentric to geocentric's own ellipsoid
/// — project the result with the engine, and compare against the value PROJ
/// 9 (the OSTN15 grid path) gives. The residual is stated, and pinned inside
/// the 0.1 m the existing control-point tests already document (ADR-0027
/// §accuracy).
/// </summary>
public sealed class TransformationGraphControlPointTests
{
    private const double ToleranceMetres = 0.1;

    [Fact]
    public async Task The_published_Helmert_reproduces_the_London_control_point()
    {
        var published = Search("EPSG:4326", "EPSG:4277")[0];
        var parameters = Helmert(published.Steps[0]);
        var (lon, lat) = Apply(
            parameters.Tx, parameters.Ty, parameters.Tz,
            parameters.Rx, parameters.Ry, parameters.Rz, parameters.ScalePpm,
            ControlPoints.London.Lon, ControlPoints.London.Lat);

        var residual = await ResidualAsync(lon, lat);

        Assert.True(
            residual <= ToleranceMetres,
            $"{published.Name} applied to the London control point lands {residual:F4} m from the " +
            $"PROJ reference, beyond the {ToleranceMetres} m the catalogue's Helmert path is stated at.");
    }

    [Fact]
    public async Task The_reduced_translation_lands_where_its_stated_accuracy_says()
    {
        var candidates = Search("EPSG:4326", "EPSG:27700");
        var reduced = candidates[^1];
        var parameters = Helmert(reduced.Steps[0]);
        var (lon, lat) = Apply(
            parameters.Tx, parameters.Ty, parameters.Tz, 0.0, 0.0, 0.0, 0.0,
            ControlPoints.London.Lon, ControlPoints.London.Lat);

        var residual = await ResidualAsync(lon, lat);

        // The candidate states what dropping the rotations costs, and the
        // control point has to agree with the statement: inside it, and
        // visibly worse than the full operation.
        Assert.True(
            residual <= reduced.AccuracyMetres,
            $"{reduced.Name} is stated at {reduced.AccuracyMetres:F1} m but misses by {residual:F1} m.");
        Assert.True(residual > ToleranceMetres, "the reduced form should be visibly worse than the full shift.");
    }

    /// <summary>The distance from a shifted OSGB 36 point to the published control point.</summary>
    private static async Task<double> ResidualAsync(double lon, double lat)
    {
        var geometry = GeometryFactory.CreatePoint(lon, lat, CoordinateReference.Epsg(4277));
        var transformed = await TransformAsync("geometry", geometry, "target", "EPSG:27700");
        var point = Assert.IsType<Point>(transformed);
        var (expectedX, expectedY) = ControlPoints.LondonBritishNationalGrid;
        return Math.Sqrt(
            (point.X!.Value - expectedX) * (point.X!.Value - expectedX)
            + (point.Y!.Value - expectedY) * (point.Y!.Value - expectedY));
    }

    /// <summary>
    /// A position-vector Helmert between two ellipsoids, written out here
    /// rather than taken from the engine so the check is independent of it.
    /// </summary>
    private static (double Lon, double Lat) Apply(
        double tx, double ty, double tz, double rx, double ry, double rz, double scalePpm,
        double lon, double lat)
    {
        var (x, y, z) = ToGeocentric(lon, lat, 6378137.0, 298.257223563);
        var toRadians = Math.PI / (180.0 * 3600.0);
        var (aX, aY, aZ) = (rx * toRadians, ry * toRadians, rz * toRadians);
        var rotation = new[,]
        {
            { Math.Cos(aZ) * Math.Cos(aY), Math.Cos(aZ) * Math.Sin(aY) * Math.Sin(aX) - Math.Sin(aZ) * Math.Cos(aX), Math.Cos(aZ) * Math.Sin(aY) * Math.Cos(aX) + Math.Sin(aZ) * Math.Sin(aX) },
            { Math.Sin(aZ) * Math.Cos(aY), Math.Sin(aZ) * Math.Sin(aY) * Math.Sin(aX) + Math.Cos(aZ) * Math.Cos(aX), Math.Sin(aZ) * Math.Sin(aY) * Math.Cos(aX) - Math.Cos(aZ) * Math.Sin(aX) },
            { -Math.Sin(aY), Math.Cos(aY) * Math.Sin(aX), Math.Cos(aY) * Math.Cos(aX) },
        };
        var scale = 1.0 + scalePpm * 1e-6;
        var shifted = new[]
        {
            scale * (rotation[0, 0] * x + rotation[0, 1] * y + rotation[0, 2] * z) + tx,
            scale * (rotation[1, 0] * x + rotation[1, 1] * y + rotation[1, 2] * z) + ty,
            scale * (rotation[2, 0] * x + rotation[2, 1] * y + rotation[2, 2] * z) + tz,
        };

        return FromGeocentric(shifted, 6377563.396, 299.3249646);
    }

    private static (double X, double Y, double Z) ToGeocentric(double lon, double lat, double semiMajor, double inverseFlattening)
    {
        var flattening = 1.0 / inverseFlattening;
        var eccentricitySquared = flattening * (2.0 - flattening);
        var (phi, lambda) = (lat * Math.PI / 180.0, lon * Math.PI / 180.0);
        var sinPhi = Math.Sin(phi);
        var radius = semiMajor / Math.Sqrt(1.0 - eccentricitySquared * sinPhi * sinPhi);
        return (
            radius * Math.Cos(phi) * Math.Cos(lambda),
            radius * Math.Cos(phi) * Math.Sin(lambda),
            radius * (1.0 - eccentricitySquared) * sinPhi);
    }

    private static (double Lon, double Lat) FromGeocentric(double[] point, double semiMajor, double inverseFlattening)
    {
        var flattening = 1.0 / inverseFlattening;
        var eccentricitySquared = flattening * (2.0 - flattening);
        var (x, y, z) = (point[0], point[1], point[2]);
        var longitude = Math.Atan2(y, x);
        var latitude = Math.Atan2(z, Math.Sqrt(x * x + y * y) * (1.0 - eccentricitySquared));
        for (var pass = 0; pass < 8; pass++)
        {
            var sinPhi = Math.Sin(latitude);
            var radius = semiMajor / Math.Sqrt(1.0 - eccentricitySquared * sinPhi * sinPhi);
            latitude = Math.Atan2(z + eccentricitySquared * radius * sinPhi, Math.Sqrt(x * x + y * y));
        }

        return (longitude * 180.0 / Math.PI, latitude * 180.0 / Math.PI);
    }

    /// <summary>
    /// A step's Helmert parameters, asserting the step is a Helmert one. A grid
    /// step publishes no seven parameters (ADR-0105), so a test that read them
    /// without saying which kind of step it held would pass for the wrong
    /// reason.
    /// </summary>
    private static HelmertParameters Helmert(CrsTransformationStep step)
    {
        Assert.Null(step.GridShift);
        return Assert.IsType<HelmertParameters>(step.Parameters);
    }
}
