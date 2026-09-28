using System.Collections.Concurrent;
using System.Globalization;
using Spatial.Contracts;
using Spatial.Core.Geometry;
using ProjCs = ProjNet.CoordinateSystems;
using ProjTf = ProjNet.CoordinateSystems.Transformations;

namespace Spatial.Transformations.ProjNet;

/// <summary>
/// The geodesic (ground-distance) buffer (ADR-0075): a local transverse
/// Mercator working plane, sized by the work's own extent plus the buffer
/// distance, wrapping <see cref="IGeometryOperations.Buffer"/> on it and
/// transforming back. ProjNet types stay inside this assembly (ADR-0005).
///
/// <para><b>Why reproject-and-buffer.</b> A true geodesic buffer walks great
/// circles and needs its own corpus at the poles and over the antimeridian.
/// Reprojecting onto a plane centred on the work is far simpler and composes
/// with a real geodesic verb later.</para>
///
/// <para><b>The stated tolerance.</b> The working plane is a transverse
/// Mercator with unit scale on the central meridian through the centre of the
/// work, so it is locally isometric there: distances are exact along that
/// meridian and the relative error grows with the square of the offset.
/// Measured against a Vincenty geodesic on WGS 84, the worst boundary
/// deviation of a geodesic circle is 0.02% of the radius at a 222 km
/// working radius (0.03% at 85°N) and 0.15% at 600 km. The implementation
/// therefore refuses a working radius above
/// <see cref="WorkingRadiusLimitMetres"/>, inside which the error stays under
/// the stated <see cref="StatedRelativeTolerance"/>. Beyond the limit the
/// caller gets <c>invalid.arguments</c> naming a projected CRS instead —
/// never a silently wrong answer.</para>
///
/// <para>Each part of a multi-part geometry is buffered in its own working
/// plane, so the tolerance follows the part's extent rather than the whole
/// collection's; the parts are dissolved in the geographic CRS afterwards,
/// which is what the planar path does in a single pass.</para>
/// </summary>
public sealed class ProjNetGeodesicBuffering : IGeodesicBuffering
{
    /// <summary>
    /// The largest working radius (the input part's envelope half-diagonal
    /// plus the buffer distance) the stated tolerance holds for: 300 km.
    /// </summary>
    public const double WorkingRadiusLimitMetres = 300_000.0;

    /// <summary>
    /// The relative distance error the engine promises inside
    /// <see cref="WorkingRadiusLimitMetres"/>: 0.05%, or 1 part in 2000.
    /// </summary>
    public const double StatedRelativeTolerance = 0.0005;

    /// <summary>Latitudes closer than this to a pole have no usable working plane.</summary>
    private const double PolarLimitDegrees = 89.0;

    private static readonly double LimitKilometres = WorkingRadiusLimitMetres / 1000.0;
    private static readonly double TolerancePercent = StatedRelativeTolerance * 100.0;

    private static readonly ProjCs.CoordinateSystemFactory Systems = new();
    private static readonly ProjTf.CoordinateTransformationFactory Transformations = new();

    /// <summary>
    /// The working planes, keyed by source code and centre. A request that
    /// buffers several features of one area — the common case — builds each
    /// plane's transforms once. Math transforms hold no per-call state, so
    /// sharing them across threads is safe.
    /// </summary>
    private static readonly ConcurrentDictionary<WorkingPlane, (ProjTf.MathTransform Forward, ProjTf.MathTransform Inverse)> Planes = new();

    private readonly IGeometryOperations planar;
    private readonly IGeometryProcessing processing;

    public ProjNetGeodesicBuffering(IGeometryOperations planar, IGeometryProcessing processing)
    {
        this.planar = planar ?? throw new ArgumentNullException(nameof(planar));
        this.processing = processing ?? throw new ArgumentNullException(nameof(processing));
    }

    public IGeometry Buffer(IGeometry geometry, double distanceMetres, int quadrantSegments = 8, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        if (!double.IsFinite(distanceMetres))
        {
            throw SpatialException.BadArguments(
                FormattableString.Invariant($"'distanceMetres' must be a finite number, got {distanceMetres}."));
        }

        if (quadrantSegments < 1)
        {
            throw SpatialException.BadArguments(
                FormattableString.Invariant($"'quadrantSegments' must be positive, got {quadrantSegments}."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var source = Source(geometry);
        var code = CataloguedCode(source);
        var geodetic = Geographic(code, source);

        var parts = BufferableParts(geometry).ToArray();
        if (parts.Length == 0)
        {
            // A collection with no members has nothing to buffer.
            return geometry;
        }

        var buffered = new IGeometry[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plane = WorkingPlane.For(parts[index], code, geodetic, distanceMetres);
            var transforms = Planes.GetOrAdd(plane, key => key.Build(Transformations));
            var projected = ProjNetTransforms.Apply(parts[index], transforms.Forward, null);
            var expanded = planar.Buffer(projected, distanceMetres, quadrantSegments, cancellationToken);
            buffered[index] = ProjNetTransforms.Apply(expanded, transforms.Inverse, source);
        }

        return buffered.Length == 1 ? buffered[0] : Dissolve(buffered, cancellationToken);
    }

    /// <summary>
    /// Reassembles the per-part results: a single area stays a polygon, a
    /// dissolved set is a multipolygon. Buffering any part yields area, so a
    /// multi-part input never buffers to a point or a line, and the dissolve
    /// is what keeps overlapping parts from self-overlapping.
    /// </summary>
    private IGeometry Dissolve(IGeometry[] buffered, CancellationToken cancellationToken)
    {
        // The union runs over parts that are already stamped with the source
        // CRS, and comes back an area in that same CRS.
        var dissolved = processing.Union(buffered, cancellationToken);
        return dissolved is Polygon or MultiPolygon
            ? dissolved
            : throw SpatialException.BadArguments(
                FormattableString.Invariant($"A ground-distance buffer dissolved to {dissolved.Type}, which is not an area."));
    }

    /// <summary>
    /// The parts worth their own working plane: a polygon (holes included) is
    /// one unit of work, and a multi-part geometry is one per member.
    /// </summary>
    private static IEnumerable<IGeometry> BufferableParts(IGeometry geometry)
    {
        if (geometry is Polygon or Point or LineString)
        {
            yield return geometry;
            yield break;
        }

        foreach (var part in geometry.Parts())
        {
            foreach (var nested in BufferableParts(part))
            {
                yield return nested;
            }
        }
    }

    private static CoordinateReference Source(IGeometry geometry) =>
        geometry.CoordinateReference
        ?? throw SpatialException.BadArguments(
            "A ground-distance buffer needs a spatial reference: the geometry carries none and the catalogue cannot guess one.");

    private static int CataloguedCode(CoordinateReference source)
    {
        if (!string.Equals(source.Authority, "EPSG", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(source.Code, NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            || !ProjEpsgCatalog.TryGet(code, out _))
        {
            throw SpatialException.BadArguments(
                FormattableString.Invariant($"A ground-distance buffer needs a CRS the catalogue serves; got {source}."));
        }

        return code;
    }

    /// <summary>The source geodetic system, rejecting a projected CRS by name.</summary>
    private static ProjCs.GeographicCoordinateSystem Geographic(int code, CoordinateReference source) =>
        ProjEpsgCatalog.TryGet(code, out var system) && system is ProjCs.GeographicCoordinateSystem geodetic
            ? geodetic
            : throw SpatialException.BadArguments(
                FormattableString.Invariant(
                    $"A ground-distance buffer needs a geographic CRS; {source} is not one in the catalogue. Buffer a projected CRS with IGeometryOperations.Buffer instead."));

    /// <summary>
    /// One working plane: the transverse Mercator through the centre of the
    /// work, carrying the source's own geodetic datum so the round trip is
    /// datum-consistent (no Helmert step in or out).
    /// </summary>
    private sealed record WorkingPlane(int Source, double CentreLongitude, double CentreLatitude)
    {
        /// <summary>
        /// Chooses and checks the plane for one part: centred on the part's
        /// envelope, sized to keep that envelope plus the buffer distance
        /// inside the tolerance the ADR states.
        /// </summary>
        public static WorkingPlane For(IGeometry part, int code, ProjCs.GeographicCoordinateSystem geodetic, double distanceMetres)
        {
            // A part with no coordinates still needs a plane to buffer in; the
            // origin is arbitrary because an empty buffer has no extent.
            var envelope = part.Envelope ?? new Envelope(0, 0, 0, 0);
            var centreLongitude = Normalise(envelope.CenterX);
            var centreLatitude = envelope.CenterY;
            if (Math.Abs(centreLatitude) > PolarLimitDegrees)
            {
                throw SpatialException.BadArguments(FormattableString.Invariant(
                    $"A ground-distance buffer has no working plane at latitude {centreLatitude:F4}°: within {PolarLimitDegrees:F0}° of a pole the meridians converge and distances along them are not comparable. Name a projected CRS and buffer there."));
            }

            var radius = WorkingRadius(envelope, centreLatitude, geodetic) + Math.Abs(distanceMetres);
            if (radius > ProjNetGeodesicBuffering.WorkingRadiusLimitMetres)
            {
                throw SpatialException.BadArguments(FormattableString.Invariant(
                    $"A ground-distance buffer here would need a {radius:F0} m working radius (the input's extent plus the distance), past the {LimitKilometres} km limit that holds the answer to {TolerancePercent:F2}% of the geodesic. Split the request, or name a projected 'bufferSR' and buffer there."));
            }

            return new WorkingPlane(code, centreLongitude, centreLatitude);
        }

        /// <summary>
        /// The half-diagonal of the part's envelope in metres, on the sphere
        /// of the source's semi-major axis — deliberately generous, so the
        /// radius check errs towards refusing rather than towards promising
        /// more accuracy than the plane delivers.
        /// </summary>
        private static double WorkingRadius(Envelope envelope, double centreLatitude, ProjCs.GeographicCoordinateSystem geodetic)
        {
            var semiMajor = geodetic.HorizontalDatum?.Ellipsoid?.SemiMajorAxis
                ?? throw SpatialException.BadArguments("The catalogue's CRS carries no ellipsoid, so it has no working plane.");
            var metresPerDegree = semiMajor * Math.PI / 180.0;
            var east = (envelope.Width / 2.0) * Math.Cos(centreLatitude * Math.PI / 180.0);
            var north = envelope.Height / 2.0;
            return Math.Sqrt((east * east) + (north * north)) * metresPerDegree;
        }

        /// <summary>The central meridian in (-180, 180], so a wrapped input still projects.</summary>
        private static double Normalise(double longitude)
        {
            var wrapped = (longitude + 180.0) % 360.0;
            if (wrapped < 0)
            {
                wrapped += 360.0;
            }

            return wrapped - 180.0;
        }

        public (ProjTf.MathTransform Forward, ProjTf.MathTransform Inverse) Build(ProjTf.CoordinateTransformationFactory factory)
        {
            ProjEpsgCatalog.TryGet(Source, out var geodetic);
            var parameters = new List<ProjCs.ProjectionParameter>
            {
                new("latitude_of_origin", CentreLatitude),
                new("central_meridian", CentreLongitude),
                new("scale_factor", 1.0),
                new("false_easting", 0.0),
                new("false_northing", 0.0),
            };
            var projection = Systems.CreateProjection("Transverse_Mercator", "Transverse_Mercator", parameters);
            var plane = Systems.CreateProjectedCoordinateSystem(
                FormattableString.Invariant($"Local TM {CentreLongitude:F6}/{CentreLatitude:F6}"),
                (ProjCs.GeographicCoordinateSystem)geodetic!,
                projection,
                ProjCs.LinearUnit.Metre,
                new ProjCs.AxisInfo("E", ProjCs.AxisOrientationEnum.East),
                new ProjCs.AxisInfo("N", ProjCs.AxisOrientationEnum.North));
            return (
                factory.CreateFromCoordinateSystems(geodetic!, plane).MathTransform,
                factory.CreateFromCoordinateSystems(plane, geodetic!).MathTransform);
        }
    }
}
