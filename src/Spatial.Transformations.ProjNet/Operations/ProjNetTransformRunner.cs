using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Transformations;
using ProjCs = ProjNet.CoordinateSystems;
using ProjTf = ProjNet.CoordinateSystems.Transformations;

namespace Spatial.Transformations.ProjNet.Operations;

/// <summary>
/// The runners for the two transformation contracts (ADR-0027): describe a
/// CRS from the <see cref="ProjEpsgCatalog"/> and transform a geometry
/// between two catalogue CRSs. Together they are the only place the plugin
/// touches the core geometry model (the transformation adapter and all ProjNet
/// types live behind this class and the catalogue). Shared pieces: argument
/// parsing (identity problems are <c>invalid.arguments</c> naming the value),
/// the engine's x-first coordinate convention (x = longitude for geographic,
/// easting for projected — the same order ProjNet 2.1's math transforms use,
/// so no axis swaps are needed), the non-finite coordinate guard and the
/// uniform failure mapping. Both capabilities are synchronous — cancellation
/// is honoured before the algorithm runs (ProjNet has no cancellation hooks).
/// </summary>
internal static class ProjNetTransformRunner
{
    private static readonly ProjTf.CoordinateTransformationFactory Transformations = new();

    public static ValueTask<CapabilityResult> DescribeAsync(CapabilityInvocation invocation)
    {
        if (!TryRequiredIdentity(invocation, TransformationArguments.Crs, out var identity, out var error)
            || !TryCatalogue(identity, out var system, out error))
        {
            return Fail(error!);
        }

        if (invocation.CancellationToken.IsCancellationRequested)
        {
            return Fail(CapabilityError.Cancelled(invocation.Capability));
        }

        try
        {
            var code = int.Parse(identity.Code, NumberStyles.None, CultureInfo.InvariantCulture);
            return Success(ProjNetCrsMapper.Describe(code, system));
        }
        catch (Exception exception)
        {
            return Fail(MapFailure(invocation.Capability, exception));
        }
    }

    public static ValueTask<CapabilityResult> TransformAsync(CapabilityInvocation invocation)
    {
        if (!TryTransformArguments(invocation, out var geometry, out var arguments, out var error))
        {
            return Fail(error!);
        }

        if (invocation.CancellationToken.IsCancellationRequested)
        {
            return Fail(CapabilityError.Cancelled(invocation.Capability));
        }

        try
        {
            var math = Transformations.CreateFromCoordinateSystems(arguments.SourceSystem, arguments.TargetSystem).MathTransform;
            var transformed = TransformGeometry(geometry!, math, Stamp(arguments.Target));
            return Success(transformed);
        }
        catch (Exception exception)
        {
            return Fail(MapFailure(invocation.Capability, exception));
        }
    }

    /// <summary>
    /// The parsed, validated transform invocation: the resolved source and
    /// target CRS systems (from the catalogue) and the identities that
    /// produced them. The geometry travels separately so this contract-side
    /// record stays free of core geometry types (the runner keeps the
    /// plugin's Core.Geometry fan-in at its planned ceiling).
    /// </summary>
    private sealed record TransformArguments(
        CrsIdentity Source,
        CrsIdentity Target,
        ProjCs.CoordinateSystem SourceSystem,
        ProjCs.CoordinateSystem TargetSystem);

    /// <summary>
    /// Parses and validates the transform arguments: the geometry, the source
    /// CRS (explicit or the geometry's own), the required target CRS and the
    /// catalogue lookups. A source argument conflicting with the geometry's
    /// own CRS identity is an invalid argument.
    /// </summary>
    private static bool TryTransformArguments(
        CapabilityInvocation invocation,
        [NotNullWhen(true)] out IGeometry? geometry,
        [NotNullWhen(true)] out TransformArguments? arguments,
        out CapabilityError? error)
    {
        geometry = null;
        arguments = null;
        error = null;
        if (!TryGeometry(invocation, out geometry, out error)
            || !TryResolveSource(invocation, geometry, out var source, out var sourceWasExplicit, out error)
            || !TryRequiredIdentity(invocation, TransformationArguments.Target, out var target, out error)
            || !TryCatalogue(source, out var sourceSystem, out error)
            || !TryCatalogue(target, out var targetSystem, out error))
        {
            return false;
        }

        if (sourceWasExplicit && geometry.CoordinateReference is { } stamped && !Same(stamped, source))
        {
            error = CapabilityError.InvalidArguments(
                $"the geometry carries CRS {stamped} but '{TransformationArguments.Source}' says {source}; "
                + "make them agree, or omit 'source' to transform from the geometry's own CRS.");
            return false;
        }

        arguments = new TransformArguments(source, target, sourceSystem, targetSystem);
        return true;
    }

    /// <summary>Reads the named geometry argument; a missing or mistyped value is an input-contract violation.</summary>
    private static bool TryGeometry(
        CapabilityInvocation invocation,
        [NotNullWhen(true)] out IGeometry? geometry,
        out CapabilityError? error)
    {
        geometry = null;
        error = null;
        if (invocation.TryGetArgument<IGeometry>(TransformationArguments.Geometry, out var value))
        {
            geometry = value;
            return true;
        }

        error = CapabilityError.InvalidArguments(
            $"{invocation.Capability} requires '{TransformationArguments.Geometry}' to carry a spatial geometry (canonical binary interchange).");
        return false;
    }

    /// <summary>
    /// Resolves the source CRS: the explicit 'source' argument when present
    /// (validated against the geometry's own CRS identity afterwards), else
    /// the geometry's own CRS — which then becomes required (ADR-0009: every
    /// geometry may state its CRS).
    /// </summary>
    private static bool TryResolveSource(
        CapabilityInvocation invocation,
        IGeometry geometry,
        out CrsIdentity source,
        out bool sourceWasExplicit,
        out CapabilityError? error)
    {
        source = default;
        sourceWasExplicit = false;
        error = null;
        if (invocation.TryGetArgument<string>(TransformationArguments.Source, out var sourceText))
        {
            if (!CrsIdentity.TryParse(sourceText, out var identity))
            {
                error = InvalidIdentity(TransformationArguments.Source, sourceText);
                return false;
            }

            source = identity;
            sourceWasExplicit = true;
            return true;
        }

        if (geometry.CoordinateReference is { } geometryCrs)
        {
            source = new CrsIdentity(geometryCrs.Authority, geometryCrs.Code);
            return true;
        }

        error = CapabilityError.InvalidArguments(
            $"'{TransformationArguments.Source}' is required when the geometry carries no CRS identity (ADR-0009).");
        return false;
    }

    /// <summary>Reads the named CRS identity argument (describe's 'crs', transform's 'target').</summary>
    private static bool TryRequiredIdentity(
        CapabilityInvocation invocation,
        string name,
        out CrsIdentity identity,
        out CapabilityError? error)
    {
        identity = default;
        error = null;
        if (!invocation.TryGetArgument<string>(name, out var text))
        {
            error = CapabilityError.InvalidArguments(
                $"{invocation.Capability} requires '{name}' to carry a CRS identity string such as 'EPSG:4326'.");
            return false;
        }

        if (!CrsIdentity.TryParse(text, out var parsed))
        {
            error = InvalidIdentity(name, text);
            return false;
        }

        identity = parsed;
        return true;
    }

    /// <summary>Resolves a CRS identity against the embedded catalogue; unknown identities are invalid arguments.</summary>
    private static bool TryCatalogue(
        CrsIdentity identity,
        [NotNullWhen(true)] out ProjCs.CoordinateSystem? system,
        out CapabilityError? error)
    {
        system = null;
        error = null;
        if (!string.Equals(identity.Authority, "EPSG", StringComparison.OrdinalIgnoreCase))
        {
            error = CapabilityError.InvalidArguments(
                $"authority '{identity.Authority}' is not served by projnet@1; the catalogue carries the EPSG subset of ADR-0027.");
            return false;
        }

        if (!int.TryParse(identity.Code, NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            || !ProjEpsgCatalog.TryGet(code, out system))
        {
            error = CapabilityError.InvalidArguments(
                $"EPSG:{identity.Code} is not in the projnet@1 catalogue; see spatial.crs.describe@1 for the served set.");
            return false;
        }

        return true;
    }

    private static bool Same(CoordinateReference stamped, CrsIdentity identity) =>
        string.Equals(stamped.Authority, identity.Authority, StringComparison.OrdinalIgnoreCase)
        && string.Equals(stamped.Code, identity.Code, StringComparison.OrdinalIgnoreCase);

    private static CoordinateReference Stamp(CrsIdentity identity) =>
        new(identity.Authority, identity.Code);

    private static CapabilityError InvalidIdentity(string name, string? text) =>
        CapabilityError.InvalidArguments(
            $"'{name}' must be a CRS identity (authority:code), got '{(text ?? "nothing")}'.");

    // ---- Geometry transformation (the plugin's only Core.Geometry surface) ----

    /// <summary>
    /// The composite geometries (multi-types and geometry collections) share
    /// one transformation path: enumerate the parts, transform each, rebuild
    /// with the target CRS on the container. The table keys the part access
    /// and the rebuild by geometry type, so the runner has no per-type branch
    /// for the four composites.
    /// </summary>
    private static readonly Dictionary<GeometryType, (Func<IGeometry, IReadOnlyList<IGeometry>> Parts, Func<IReadOnlyList<IGeometry>, CoordinateReference?, IGeometry> Build)> Composites = new()
    {
        [GeometryType.MultiPoint] = (geometry => ((MultiPoint)geometry).Points, (parts, crs) => new MultiPoint(parts.Cast<Point>(), crs)),
        [GeometryType.MultiLineString] = (geometry => ((MultiLineString)geometry).LineStrings, (parts, crs) => new MultiLineString(parts.Cast<LineString>(), crs)),
        [GeometryType.MultiPolygon] = (geometry => ((MultiPolygon)geometry).Polygons, (parts, crs) => new MultiPolygon(parts.Cast<Polygon>(), crs)),
        [GeometryType.GeometryCollection] = (geometry => ((GeometryCollection)geometry).Geometries, (parts, crs) => new GeometryCollection(parts, crs)),
    };

    /// <summary>
    /// Recursively transforms a geometry, stamping the result's CRS only at
    /// the top level (parts and rings carry none, matching the codec's
    /// convention). Empty geometries keep their type and layout.
    /// </summary>
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

    /// <summary>Transforms a composite by mapping its parts through the same recursive transform.</summary>
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

    /// <summary>
    /// Transforms one XY ordinate pair, keeping Z and M. ProjNet 2.1's math
    /// transforms consume and produce (x, y) — longitude/easting first — the
    /// engine's convention, so no axis swap happens here. A non-finite result
    /// (input outside the projection's valid area) is an actionable error
    /// rather than silent NaN geometry.
    /// </summary>
    private static Coordinate TransformCoordinate(Coordinate coordinate, ProjTf.MathTransform math)
    {
        var (x, y) = math.Transform(coordinate.X, coordinate.Y);
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new TransformOutOfRangeException(
                $"transforming ({coordinate.X}, {coordinate.Y}) produced non-finite coordinates ({x}, {y}); "
                + "the point falls outside the target CRS's valid area.");
        }

        return coordinate with { X = x, Y = y };
    }

    /// <summary>
    /// An empty geometry of the same type and layout in the target CRS:
    /// points, lines and polygons keep their layout; composites rebuild
    /// empty through the composite tables.
    /// </summary>
    private static IGeometry EmptyLike(IGeometry geometry, CoordinateReference? target) => geometry.Type switch
    {
        GeometryType.Point => GeometryFactory.CreateEmptyPoint(target, ((Point)geometry).Layout),
        GeometryType.LineString => GeometryFactory.CreateEmptyLineString(((LineString)geometry).Layout, target),
        GeometryType.Polygon => new Polygon(GeometryFactory.CreateEmptyLineString(((Polygon)geometry).ExteriorRing.Layout), null, target),
        _ => Composites[geometry.Type].Build([], target),
    };

    /// <summary>A transformed coordinate that landed outside the target CRS's valid area.</summary>
    private sealed class TransformOutOfRangeException(string message) : Exception(message)
    {
    }

    /// <summary>
    /// Uniform failure mapping (ADR-0027): input problems — unknown
    /// identities, ProjNet's "no supported transformation path", out-of-area
    /// coordinates — are <c>invalid.arguments</c>; anything else is a
    /// provider failure.
    /// </summary>
    private static CapabilityError MapFailure(CapabilityId capability, Exception exception)
    {
        if (exception is TransformOutOfRangeException or ArgumentException or NotSupportedException or FormatException)
        {
            return CapabilityError.InvalidArguments($"{capability} could not process the input: {exception.Message}");
        }

        return CapabilityError.ProviderFailure(
            $"{capability} failed unexpectedly while running the ProjNet transformation: {exception.Message}");
    }

    private static ValueTask<CapabilityResult> Fail(CapabilityError error) =>
        new(CapabilityResult.Failure(error));

    private static ValueTask<CapabilityResult> Success(object value) =>
        new(CapabilityResult.Success(value));
}
