using System.Collections.Concurrent;
using System.Globalization;
using Spatial.Contracts;
using Spatial.Contracts.Transformations;
using Spatial.Contracts.TransformationSearch;
using Spatial.Core.Geometry;
using Spatial.Transformations.ProjNet.Grids;
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

    /// <summary>
    /// The hot-path cache (T-087): building a math transform from a CRS
    /// pair dominates single-point transforms (38.7x vs raw ProjNet in the
    /// T-076 micro), so each EPSG pair's transform is built once and shared.
    /// ProjNet math transforms hold no per-call state, making them safe to
    /// share across threads; misses build outside any lock via
    /// <see cref="ConcurrentDictionary{TKey, TValue}"/>. Bounded by the
    /// catalogue's CRS pairs.
    /// </summary>
    private static readonly ConcurrentDictionary<(int Source, int Target), ProjTf.MathTransform> MathTransforms = new();

    /// <summary>
    /// The datum shift grids this host was configured with (ADR-0105). A host
    /// with no configured grid directory holds the empty registry, and every
    /// datum is shifted the classic Helmert way exactly as before — the grid
    /// path is additive, and a host that deploys nothing loses nothing.
    /// </summary>
    private readonly DatumShiftGridRegistry _grids;

    public ProjNetTransforms()
        : this([])
    {
    }

    /// <summary>
    /// Builds the transformation service over the datum shift grids found in
    /// the given directories, in priority order (ADR-0105). The host passes
    /// its configured directories and nothing else: the registry that resolves
    /// them stays inside this assembly, because it is a detail of this
    /// provider rather than something a composition root should hold.
    /// </summary>
    public ProjNetTransforms(IEnumerable<string> gridDirectories)
        : this(DatumShiftGridRegistry.Load([.. gridDirectories]))
    {
    }

    internal ProjNetTransforms(DatumShiftGridRegistry grids) =>
        _grids = grids ?? DatumShiftGridRegistry.Empty;

    /// <summary>Test pin (T-087): the cached math transform for a pair, when present.</summary>
    internal static bool TryGetCachedMathTransform(int sourceCode, int targetCode, out ProjTf.MathTransform? math) =>
        MathTransforms.TryGetValue((sourceCode, targetCode), out math);

    /// <summary>
    /// The grid-backed plan for a CRS pair, cached per host and built on first
    /// use (ADR-0107). A pair with no deployed grid has no plan at all, which
    /// is what keeps a host that deploys nothing on exactly the path it was on
    /// before. The cache is per instance rather than static because the plans
    /// name the grids this host was configured with, and two hosts in one
    /// process may be configured differently.
    /// </summary>
    private readonly ConcurrentDictionary<(int Source, int Target), GridShiftPlan?> _plans = new();

    private bool GridShiftPlanFor((int Source, int Target) key, out GridShiftPlan plan)
    {
        if (!_plans.TryGetValue(key, out var found))
        {
            found = Build(key.Source, key.Target);
            _plans[key] = found;
        }

        plan = found!;
        return found is not null;
    }

    private GridShiftPlan? Build(int sourceCode, int targetCode)
    {
        if (!ProjEpsgCatalog.TryGetDatum(sourceCode, out var from) || !ProjEpsgCatalog.TryGetDatum(targetCode, out var to))
        {
            // A datum the graph does not publish has no shift to replace, so
            // there is nothing for a grid to do on this pair.
            return null;
        }

        return GridShiftPlan.For(sourceCode, targetCode, from!, to!, _grids);
    }

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

    /// <summary>
    /// The datum-transformation search (ADR-0087). The graph is keyed on
    /// datums, so a projected CRS and its geographic parent return the same
    /// candidates; an unknown CRS identity fails exactly as
    /// <see cref="Describe"/> does, and cancellation is honoured before any
    /// work.
    /// </summary>
    public IReadOnlyList<CrsTransformation> FindTransformations(
        CrsTransformationQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var from = DatumOf(query.Source);
        var to = DatumOf(query.Target);
        cancellationToken.ThrowIfCancellationRequested();
        return DatumTransformationGraph.Search(from, to, _grids, query.AreaOfInterest);
    }

    /// <summary>
    /// The datum a CRS identity is on. "No such CRS" and "no transformation
    /// needed" are different answers, so an identity the catalogue does not
    /// serve fails here rather than answering with an empty list.
    /// </summary>
    private static DatumNode DatumOf(string crs)
    {
        if (!CrsIdentity.TryParse(crs, out var identity))
        {
            throw SpatialException.BadArguments($"'{crs}' must be a CRS identity (authority:code), got '{crs ?? "nothing"}'.");
        }

        if (!string.Equals(identity.Authority, "EPSG", StringComparison.OrdinalIgnoreCase))
        {
            throw SpatialException.BadArguments($"Authority/code '{crs}' is not in the catalogue.");
        }

        var code = int.Parse(identity.Code, NumberStyles.None, CultureInfo.InvariantCulture);
        return ProjEpsgCatalog.TryGetDatum(code, out var datum)
            ? datum
            : throw SpatialException.BadArguments($"Authority/code '{crs}' is not in the catalogue.");
    }

    public IGeometry Transform(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        cancellationToken.ThrowIfCancellationRequested();
        var sourceIdentity = ResolveSource(geometry, source);
        var targetIdentity = ResolveTarget(target);

        var sourceSystem = Lookup(sourceIdentity);
        var targetSystem = Lookup(targetIdentity);
        EnsureSourceMatchesStamp(geometry, source, sourceIdentity);

        // Lookup has validated both identities as known EPSG codes, so the
        // parse cannot fail; the int keys canonicalise spelling variants
        // ("epsg:4326" shares the "EPSG:4326" entry).
        var key = (
            Source: int.Parse(sourceIdentity.Code, NumberStyles.None, CultureInfo.InvariantCulture),
            Target: int.Parse(targetIdentity.Code, NumberStyles.None, CultureInfo.InvariantCulture));

        try
        {
            var stamped = new CoordinateReference(targetIdentity.Authority, targetIdentity.Code);

            // A deployed grid takes the point away from ProjNet's single
            // composition and onto the explicit one (ADR-0107). With no bundle
            // there is no plan, and the path below is the one this host has
            // always run.
            if (GridShiftPlanFor(key, out var plan))
            {
                return ApplyPerCoordinate(geometry, plan, stamped, cancellationToken);
            }

            if (!MathTransforms.TryGetValue(key, out var math))
            {
                math = MathTransforms.GetOrAdd(key, _ => Transformations.CreateFromCoordinateSystems(sourceSystem, targetSystem).MathTransform);
            }

            return Apply(geometry, math, stamped);
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

    private static CrsIdentity ResolveTarget(string target)
    {
        if (!CrsIdentity.TryParse(target, out var identity))
        {
            throw SpatialException.BadArguments($"'target' must be a CRS identity (authority:code), got '{target ?? "nothing"}'.");
        }

        return identity;
    }

    private static void EnsureSourceMatchesStamp(IGeometry geometry, string? source, CrsIdentity sourceIdentity)
    {
        if (source is not null && geometry.CoordinateReference is { } stamped
            && (!string.Equals(stamped.Authority, sourceIdentity.Authority, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(stamped.Code, sourceIdentity.Code, StringComparison.OrdinalIgnoreCase)))
        {
            throw SpatialException.BadArguments(
                $"The geometry carries CRS {stamped} but 'source' says {sourceIdentity}; make them agree, or omit 'source'.");
        }
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

    /// <summary>
    /// Applies a grid-backed plan to every coordinate. The walk is per
    /// coordinate because the plan is: a geometry is free to cross the edge of
    /// a grid's block, and every one of its coordinates is answered by
    /// whichever operation covers that coordinate (ADR-0107).
    /// </summary>
    private static IGeometry ApplyPerCoordinate(
        IGeometry geometry,
        GridShiftPlan plan,
        CoordinateReference? target,
        CancellationToken cancellationToken) =>
        Walk(geometry, coordinate => ShiftCoordinate(plan, coordinate), target, cancellationToken);

    private static Coordinate ShiftCoordinate(GridShiftPlan plan, Coordinate coordinate)
    {
        var (x, y) = plan.Shift(coordinate.X, coordinate.Y);
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw SpatialException.BadArguments(
                $"Transforming ({coordinate.X}, {coordinate.Y}) produced non-finite coordinates; the point falls outside the target CRS's valid area.");
        }

        return coordinate with { X = x, Y = y };
    }

    private static readonly Dictionary<GeometryType, (Func<IGeometry, IReadOnlyList<IGeometry>> Parts, Func<IReadOnlyList<IGeometry>, CoordinateReference?, IGeometry> Build)> Composites = new()
    {
        [GeometryType.MultiPoint] = (geometry => ((MultiPoint)geometry).Points, (parts, crs) => new MultiPoint(parts.Cast<Point>(), crs)),
        [GeometryType.MultiLineString] = (geometry => ((MultiLineString)geometry).LineStrings, (parts, crs) => new MultiLineString(parts.Cast<LineString>(), crs)),
        [GeometryType.MultiPolygon] = (geometry => ((MultiPolygon)geometry).Polygons, (parts, crs) => new MultiPolygon(parts.Cast<Polygon>(), crs)),
        [GeometryType.GeometryCollection] = (geometry => ((GeometryCollection)geometry).Geometries, (parts, crs) => new GeometryCollection(parts, crs)),
    };

    /// <summary>
    /// Applies a math transform to every coordinate, preserving shapes, empty
    /// geometries, layouts and Z/M ordinates, and stamping the target CRS.
    /// Internal so the geodesic buffer (ADR-0075) can run the same
    /// transformation over its own working plane, which is not a catalogue
    /// CRS and so cannot go through <see cref="Transform"/>.
    /// </summary>
    internal static IGeometry Apply(IGeometry geometry, ProjTf.MathTransform math, CoordinateReference? target) =>
        Walk(geometry, coordinate => TransformCoordinate(coordinate, math), target, CancellationToken.None);

    /// <summary>
    /// The one walk both paths take: every coordinate replaced by
    /// <paramref name="shift"/>, shapes, empty geometries, layouts and Z/M
    /// ordinates preserved, and the target CRS stamped. Keeping it single is
    /// what stops the grid-backed path (ADR-0107) and the ProjNet path from
    /// differing in anything but the answer they give a coordinate.
    /// <para>
    /// Cancellation is honoured at the top of the walk and periodically inside
    /// it, because on the grid path a large geometry is a long operation and a
    /// client that asked to stop should not wait for the whole of it. A path
    /// given <see cref="CancellationToken.None"/> never pays for the check.
    /// </para>
    /// </summary>
    private static IGeometry Walk(
        IGeometry geometry,
        Func<Coordinate, Coordinate> shift,
        CoordinateReference? target,
        CancellationToken cancellationToken)
    {
        if (geometry.IsEmpty)
        {
            return EmptyLike(geometry, target);
        }

        return geometry switch
        {
            Point point => new Point(point.Coordinate is { } coordinate ? shift(coordinate) : null, target),
            LineString line => new LineString(ShiftSequence(line.Sequence, shift, cancellationToken), target),
            Polygon polygon => ShiftPolygon(polygon, shift, target, cancellationToken),
            _ => ShiftComposite(geometry, shift, target, cancellationToken),
        };
    }

    private static IGeometry ShiftComposite(
        IGeometry geometry,
        Func<Coordinate, Coordinate> shift,
        CoordinateReference? target,
        CancellationToken cancellationToken)
    {
        var (parts, build) = Composites[geometry.Type];
        var transformed = parts(geometry).Select(part => Walk(part, shift, null, cancellationToken)).ToArray();
        return build(transformed, target);
    }

    private static Polygon ShiftPolygon(
        Polygon polygon,
        Func<Coordinate, Coordinate> shift,
        CoordinateReference? target,
        CancellationToken cancellationToken) =>
        new(
            ShiftLine(polygon.ExteriorRing, shift, cancellationToken),
            polygon.InteriorRings.Select(ring => ShiftLine(ring, shift, cancellationToken)),
            target);

    private static LineString ShiftLine(
        LineString line,
        Func<Coordinate, Coordinate> shift,
        CancellationToken cancellationToken) =>
        new(ShiftSequence(line.Sequence, shift, cancellationToken));

    private static PackedCoordinateSequence ShiftSequence(
        ICoordinateSequence sequence,
        Func<Coordinate, Coordinate> shift,
        CancellationToken cancellationToken)
    {
        var coordinates = new Coordinate[sequence.Count];
        for (var index = 0; index < sequence.Count; index++)
        {
            if (cancellationToken.CanBeCanceled && index % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            coordinates[index] = shift(sequence.GetCoordinate(index));
        }

        return PackedCoordinateSequence.FromCoordinates(coordinates, sequence.Layout);
    }

    /// <summary>
    /// How many coordinates are shifted between cancellation checks. Checking
    /// every coordinate would cost more than the check is worth, and every few
    /// hundred bounds a cancellation to work a client cannot tell from the work
    /// already done.
    /// </summary>
    private const int CancellationCheckInterval = 256;

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
