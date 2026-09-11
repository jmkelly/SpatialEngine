using System.Globalization;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Transformations;
using ProjCs = ProjNet.CoordinateSystems;
using ProjTf = ProjNet.CoordinateSystems.Transformations;

namespace Spatial.Transformations.ProjNet;

/// <summary>
/// The ProjNet coordinate service (ADR-0033): a direct, in-process
/// implementation of <see cref="ICrsDirectory"/> and
/// <see cref="ICoordinateTransforms"/> on ProjNet 2.1 with the curated
/// embedded EPSG catalogue. ProjNet types stay inside this assembly
/// (ADR-0005). Axis order is x-first for every CRS. Unknown identities and
/// out-of-area coordinates throw <see cref="SpatialException"/> with code
/// <c>invalid.arguments</c>.
/// </summary>
public sealed class ProjNetTransforms : ICrsDirectory, ICoordinateTransforms
{
    private static readonly ProjTf.CoordinateTransformationFactory Transformations = new();

    public CrsDescription Describe(string crs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CrsIdentity.TryParse(crs, out var identity))
        {
            throw SpatialException.BadArguments($"'crs' must be a CRS identity (authority:code), got '{crs ?? "nothing"}'.");
        }

        var system = Lookup(identity);
        var code = int.Parse(identity.Code, NumberStyles.None, CultureInfo.InvariantCulture);
        return ProjNetCrsMapper.Describe(code, system);
    }

    public IGeometry Transform(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceIdentity = ResolveSource(geometry, source);
        if (!CrsIdentity.TryParse(target, out var targetIdentity))
        {
            throw SpatialException.BadArguments($"'target' must be a CRS identity (authority:code), got '{target ?? "nothing"}'.");
        }

        var sourceSystem = Lookup(sourceIdentity);
        var targetSystem = Lookup(targetIdentity);
        if (source is not null && geometry.CoordinateReference is { } stamped
            && (!string.Equals(stamped.Authority, sourceIdentity.Authority, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(stamped.Code, sourceIdentity.Code, StringComparison.OrdinalIgnoreCase)))
        {
            throw SpatialException.BadArguments(
                $"The geometry carries CRS {stamped} but 'source' says {sourceIdentity}; make them agree, or omit 'source'.");
        }

        try
        {
            var math = Transformations.CreateFromCoordinateSystems(sourceSystem, targetSystem).MathTransform;
            return TransformGeometry(geometry, math, new CoordinateReference(targetIdentity.Authority, targetIdentity.Code));
        }
        catch (SpatialException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or FormatException)
        {
            throw SpatialException.BadArguments($"The transform could not process the input: {exception.Message}");
        }
    }

    private static CrsIdentity ResolveSource(IGeometry geometry, string? source)
    {
        if (source is not null)
        {
            if (!CrsIdentity.TryParse(source, out var identity))
            {
                throw SpatialException.BadArguments($"'source' must be a CRS identity (authority:code), got '{source}'.");
            }

            return identity;
        }

        if (geometry.CoordinateReference is { } crs)
        {
            return new CrsIdentity(crs.Authority, crs.Code);
        }

        throw SpatialException.BadArguments("'source' is required when the geometry carries no CRS identity.");
    }

    private static ProjCs.CoordinateSystem Lookup(CrsIdentity identity)
    {
        if (!string.Equals(identity.Authority, "EPSG", StringComparison.OrdinalIgnoreCase))
        {
            throw SpatialException.BadArguments($"Authority '{identity.Authority}' is not served; the catalogue carries an EPSG subset.");
        }

        if (!int.TryParse(identity.Code, NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            || !ProjEpsgCatalog.TryGet(code, out var system))
        {
            throw SpatialException.BadArguments($"EPSG:{identity.Code} is not in the catalogue.");
        }

        return system;
    }

    private static readonly Dictionary<GeometryType, (Func<IGeometry, IReadOnlyList<IGeometry>> Parts, Func<IReadOnlyList<IGeometry>, CoordinateReference?, IGeometry> Build)> Composites = new()
    {
        [GeometryType.MultiPoint] = (geometry => ((MultiPoint)geometry).Points, (parts, crs) => new MultiPoint(parts.Cast<Point>(), crs)),
        [GeometryType.MultiLineString] = (geometry => ((MultiLineString)geometry).LineStrings, (parts, crs) => new MultiLineString(parts.Cast<LineString>(), crs)),
        [GeometryType.MultiPolygon] = (geometry => ((MultiPolygon)geometry).Polygons, (parts, crs) => new MultiPolygon(parts.Cast<Polygon>(), crs)),
        [GeometryType.GeometryCollection] = (geometry => ((GeometryCollection)geometry).Geometries, (parts, crs) => new GeometryCollection(parts, crs)),
    };

    private static IGeometry TransformGeometry(IGeometry geometry, ProjTf.MathTransform math, CoordinateReference? target)
    {
        if (geometry.IsEmpty)
        {
            return EmptyLike(geometry, target);
        }

        return geometry switch
        {
            Point point => new Point(point.Coordinate is { } coordinate ? TransformCoordinate(coordinate, math) : null, target),
            LineString line => new LineString(TransformSequence(line.Sequence, math), target),
            Polygon polygon => TransformPolygon(polygon, math, target),
            _ => TransformComposite(geometry, math, target),
        };
    }

    private static IGeometry TransformComposite(IGeometry geometry, ProjTf.MathTransform math, CoordinateReference? target)
    {
        var (parts, build) = Composites[geometry.Type];
        var transformed = parts(geometry).Select(part => TransformGeometry(part, math, null)).ToArray();
        return build(transformed, target);
    }

    private static Polygon TransformPolygon(Polygon polygon, ProjTf.MathTransform math, CoordinateReference? target) =>
        new(
            TransformLine(polygon.ExteriorRing, math),
            polygon.InteriorRings.Select(ring => TransformLine(ring, math)),
            target);

    private static LineString TransformLine(LineString line, ProjTf.MathTransform math) =>
        new(TransformSequence(line.Sequence, math));

    private static PackedCoordinateSequence TransformSequence(ICoordinateSequence sequence, ProjTf.MathTransform math)
    {
        var coordinates = new Coordinate[sequence.Count];
        for (var index = 0; index < sequence.Count; index++)
        {
            coordinates[index] = TransformCoordinate(sequence.GetCoordinate(index), math);
        }

        return PackedCoordinateSequence.FromCoordinates(coordinates, sequence.Layout);
    }

    private static Coordinate TransformCoordinate(Coordinate coordinate, ProjTf.MathTransform math)
    {
        var (x, y) = math.Transform(coordinate.X, coordinate.Y);
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw SpatialException.BadArguments(
                $"Transforming ({coordinate.X}, {coordinate.Y}) produced non-finite coordinates; the point falls outside the target CRS's valid area.");
        }

        return coordinate with { X = x, Y = y };
    }

    private static IGeometry EmptyLike(IGeometry geometry, CoordinateReference? target) => geometry.Type switch
    {
        GeometryType.Point => GeometryFactory.CreateEmptyPoint(target, ((Point)geometry).Layout),
        GeometryType.LineString => GeometryFactory.CreateEmptyLineString(((LineString)geometry).Layout, target),
        GeometryType.Polygon => new Polygon(GeometryFactory.CreateEmptyLineString(((Polygon)geometry).ExteriorRing.Layout), null, target),
        _ => Composites[geometry.Type].Build([], target),
    };
}
