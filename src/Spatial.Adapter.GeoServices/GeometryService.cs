using System.Globalization;
using System.Text.Json;
using Spatial.Contracts;
using Spatial.Contracts.Transformations;
using Spatial.Contracts.TransformationSearch;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Geometry Service operations (spec §7) the engine can satisfy, mapping
/// protocol arguments to engine verbs — never by name alone. The adapter owns
/// no algorithms: <c>project</c> calls <see cref="ICoordinateTransforms"/>,
/// the planar verbs call <see cref="IGeometryOperations"/>,
/// <see cref="IGeometryMeasures"/>, <see cref="IGeometryProcessing"/> and
/// <see cref="IGeometryRelations"/>. The semantic trap is explicit:
/// <c>generalize</c> is Douglas-Peucker (Simplify) and <c>simplify</c> is
/// topological repair (Repair) — never the same verb.
/// </summary>
internal static class GeometryService
{
    private const double CurrentVersion = 10.0;

    /// <summary>Dispatches one operation by its GeoServices name.</summary>
    public static IResult Dispatch(
        string operation,
        EsriRequestParameters parameters,
        GeometryServiceCapabilities capabilities,
        CancellationToken cancellationToken) =>
        Operations.TryGetValue(operation, out var handler)
            ? handler(parameters, capabilities, cancellationToken)
            : throw GeoServicesErrors.Invalid($"The Geometry Service operation '{operation}' is not supported.");

    private static readonly Dictionary<string, Func<EsriRequestParameters, GeometryServiceCapabilities, CancellationToken, IResult>> Operations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["project"] = (parameters, capabilities, token) => Project(parameters, capabilities, token),
            ["generalize"] = (parameters, capabilities, token) => Generalize(parameters, capabilities.Operations, token),
            ["buffer"] = (parameters, capabilities, token) => Buffer(parameters, capabilities, token),
            ["intersect"] = (parameters, capabilities, token) => Intersect(parameters, capabilities.Operations, token),
            ["areasandlengths"] = (parameters, capabilities, token) => AreasAndLengths(parameters, capabilities.Measures, token),
            ["lengths"] = (parameters, capabilities, token) => Lengths(parameters, capabilities.Measures, token),
            ["distance"] = (parameters, capabilities, token) => Distance(parameters, capabilities.Measures, token),
            ["convexhull"] = (parameters, capabilities, token) => ConvexHull(parameters, capabilities.Processing, token),
            ["difference"] = (parameters, capabilities, token) => Difference(parameters, capabilities.Processing, token),
            ["union"] = (parameters, capabilities, token) => Union(parameters, capabilities.Processing, token),
            ["simplify"] = (parameters, capabilities, token) => Simplify(parameters, capabilities.Processing, token),
            ["relation"] = (parameters, capabilities, token) => Relation(parameters, capabilities.Relations, token),
            ["densify"] = (parameters, capabilities, token) => Densify(parameters, capabilities.Processing, token),
            ["labelpoints"] = (parameters, capabilities, token) => LabelPoints(parameters, capabilities.Measures, token),
            ["findtransformations"] = (parameters, capabilities, token) => FindTransformations(parameters, capabilities, token),
            ["fromgeocoordinatestring"] = (_, _, _) => throw GeoServicesErrors.Invalid(
                "The 'fromGeoCoordinateString' operation is not supported: the engine has no coordinate-notation codec " +
                "(MGRS/USNG/UTM/GeoRef/GARS/DMS/DDM/DD) and half-parsing notations is a deliberate non-goal."),
            ["togeocoordinatestring"] = (_, _, _) => throw GeoServicesErrors.Invalid(
                "The 'toGeoCoordinateString' operation is not supported: the engine has no coordinate-notation codec " +
                "(MGRS/USNG/UTM/GeoRef/GARS/DMS/DDM/DD) and half-parsing notations is a deliberate non-goal."),
        };

    /// <summary>The Geometry Service resource metadata (spec §7.0.1).</summary>
    public static IResult Info() =>
        EsriJson.Value(new GeometryServerInfo(
            CurrentVersion,
            "SpatialEngine Geometry Service",
            "Project,Generalize,Buffer,Intersect,AreasAndLengths,Lengths,Distance,ConvexHull,Difference,Union,Simplify,Relation,Densify,LabelPoints,FindTransformations"));

    private static IResult Project(EsriRequestParameters parameters, GeometryServiceCapabilities capabilities, CancellationToken cancellationToken)
    {
        var source = EsriValueParser.ParseSpatialReference(parameters.Get("inSR"));
        var target = EsriValueParser.ParseSpatialReference(parameters.Require("outSR"))
            ?? throw GeoServicesErrors.Invalid("'outSR' is required for project.");
        // findTransformations publishes the ranked operations between two
        // CRSs; project applies the one it ranks first, so naming that
        // operation back is honoured. Any other candidate is refused by name,
        // with the path the engine does apply, rather than projected as if it
        // had been: the catalogue carries Helmert operations only, and the
        // registered transform is the composed geocentric path.
        var named = parameters.Get("datumTransformation");
        if (!string.IsNullOrWhiteSpace(named))
        {
            EnsureAppliedTransformation(named, source, target, capabilities, cancellationToken);
        }

        var geometries = EsriValueParser.ParseGeometries(parameters.Require("geometries"), source);
        var results = geometries
            .Select(geometry => capabilities.Transforms.Transform(geometry, source?.ToString(), target.ToString(), cancellationToken))
            .ToArray();
        return Geometries(results);
    }

    private static void EnsureAppliedTransformation(
        string named,
        CoordinateReference? source,
        CoordinateReference target,
        GeometryServiceCapabilities capabilities,
        CancellationToken cancellationToken)
    {
        var candidates = SearchTransformations(source, target, capabilities, null, cancellationToken);
        var applied = candidates.Count == 0 ? null : candidates[0];
        if (applied is not null && string.Equals(applied.Name, named, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var available = candidates.Count == 0
            ? "the catalogue lists no datum transformation for this pair"
            : $"project applies '{applied!.Name}'";
        throw GeoServicesErrors.Invalid(
            $"The 'datumTransformation' parameter names '{named}', which project does not apply: {available}. " +
            "The catalogue carries Helmert operations only, so no alternative is projected in its place; " +
            "call findTransformations for the ranked candidates and their accuracies.");
    }

    private static IResult Generalize(EsriRequestParameters parameters, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        var spatialReference = EsriValueParser.ParseSpatialReference(parameters.Get("sr"));
        var deviation = ParseDouble(parameters.Require("maxDeviation"), "maxDeviation");
        var geometries = EsriValueParser.ParseGeometries(parameters.Require("geometries"), spatialReference);
        return Geometries(geometries.Select(geometry => operations.Simplify(geometry, deviation, cancellationToken)));
    }

    private static IResult Buffer(EsriRequestParameters parameters, GeometryServiceCapabilities capabilities, CancellationToken cancellationToken)
    {
        // Spec §7.0.6: buffered in bufferSR ?? outSR ?? inSR, returned in
        // outSR ?? bufferSR ?? inSR. The engine buffers planar, so a linear
        // unit against a geographic buffer CRS stays rejected (no geodesic
        // verb); with a projected bufferSR the request is a
        // transform-then-buffer via ICoordinateTransforms.
        RejectUnsupportedBufferOptions(parameters);
        var fallback = EsriValueParser.ParseSpatialReference(parameters.Get("inSR"))
            ?? EsriValueParser.ParseSpatialReference(parameters.Get("sr"));
        var geometries = EsriValueParser.ParseGeometries(parameters.Require("geometries"), fallback);
        var work = new BufferWork(
            geometries,
            fallback,
            EsriValueParser.ParseSpatialReference(parameters.Get("bufferSR")),
            EsriValueParser.ParseSpatialReference(parameters.Get("outSR")),
            EsriValueParser.ParseDoubles(parameters.Require("distances"), "distances"),
            ParseOptionalInt(parameters, "quadrantSegments", 8));
        if (work.Distances.Count != 1 && work.Distances.Count != work.Geometries.Count)
        {
            throw GeoServicesErrors.Invalid("'distances' must hold one value or one value per input geometry.");
        }

        var results = new IGeometry[work.Geometries.Count];
        for (var i = 0; i < work.Geometries.Count; i++)
        {
            results[i] = BufferOne(work, i, parameters, capabilities, cancellationToken);
        }

        return Geometries(results);
    }

    private static void RejectUnsupportedBufferOptions(EsriRequestParameters parameters)
    {
        if (parameters.GetBool("geodesic", false))
        {
            throw GeoServicesErrors.Invalid("The 'geodesic' parameter is not supported: the engine buffers planar; project first (bufferSR) and buffer without geodesic.");
        }

        // unionResults=false is the Esri default and matches engine behavior
        // (one result array per input), so it is accepted leniently like
        // geodesic=false; unionResults=true has no engine verb and stays
        // rejected by name.
        if (parameters.GetBool("unionResults", false))
        {
            throw GeoServicesErrors.Invalid("The 'unionResults' parameter is not supported: unionResults is not supported; the result is an array per input.");
        }
    }

    private sealed record BufferWork(
        IReadOnlyList<IGeometry> Geometries,
        CoordinateReference? Fallback,
        CoordinateReference? BufferSr,
        CoordinateReference? OutSr,
        IReadOnlyList<double> Distances,
        int Segments);

    private static IGeometry BufferOne(BufferWork work, int index, EsriRequestParameters parameters, GeometryServiceCapabilities capabilities, CancellationToken cancellationToken)
    {
        // CRS-less inputs are interpreted in the buffer CRS, as before.
        var source = BufferSource(work, index);
        var bufferCrs = work.BufferSr ?? work.OutSr ?? source;
        var distance = (work.Distances.Count == 1 ? work.Distances[0] : work.Distances[index])
            * BufferDistanceFactor(parameters, capabilities.Catalogue, bufferCrs, cancellationToken);
        var working = DiffersFrom(bufferCrs, source)
            ? capabilities.Transforms.Transform(work.Geometries[index], source?.ToString(), bufferCrs!.Value.ToString(), cancellationToken)
            : work.Geometries[index];
        var buffered = capabilities.Operations.Buffer(working, distance, work.Segments, cancellationToken);
        var target = BufferTarget(work, source);
        return DiffersFrom(target, bufferCrs)
            ? capabilities.Transforms.Transform(buffered, bufferCrs?.ToString(), target!.Value.ToString(), cancellationToken)
            : buffered;
    }

    private static CoordinateReference? BufferTarget(BufferWork work, CoordinateReference? source) =>
        work.OutSr ?? work.BufferSr ?? source;

    private static CoordinateReference? BufferSource(BufferWork work, int index) =>
        work.Geometries[index].CoordinateReference ?? work.Fallback ?? work.BufferSr ?? work.OutSr;

    private static bool DiffersFrom(CoordinateReference? left, CoordinateReference? right) =>
        left.HasValue && left.Value != right;

    /// <summary>
    /// Converts one raw <c>distances</c> value into buffer-CRS units. Without
    /// <c>unit</c> the distances are already in buffer-CRS units (metres for
    /// the catalogue's projected CRSs, degrees for geographic ones); with a
    /// linear <c>unit</c> the buffer CRS must be projected (metres), with an
    /// angular <c>unit</c> it must be geographic (degrees).
    /// </summary>
    private static double BufferDistanceFactor(EsriRequestParameters parameters, ICrsDirectory catalogue, CoordinateReference? bufferCrs, CancellationToken cancellationToken)
    {
        var raw = parameters.Get("unit");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 1.0;
        }

        var code = ParseUnitCode(raw);
        if (bufferCrs is null)
        {
            throw GeoServicesErrors.Invalid("'unit' requires a spatial reference: name 'bufferSR' (or 'inSR'/'sr') so distances have units.");
        }

        var kind = catalogue.Describe(bufferCrs.Value.ToString(), cancellationToken).Kind;
        return ResolveUnitFactor(code, bufferCrs, kind);
    }

    /// <summary>
    /// Parses the <c>unit</c> parameter to a curated Esri unit code, rejecting
    /// non-numeric codes and codes outside the curated linear/angular tables.
    /// </summary>
    internal static int ParseUnitCode(string raw)
    {
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            throw GeoServicesErrors.Invalid($"'unit' must be a numeric Esri unit code, got '{raw}'. Supported: {EsriUnits.DescribeSupported()}.");
        }

        var angular = EsriUnits.IsAngular(code);
        if (!angular && !EsriUnits.TryGetLinear(code, out _, out _)
            || angular && !EsriUnits.TryGetAngular(code, out _, out _))
        {
            throw GeoServicesErrors.Invalid($"Unit code {code} is not in the curated unit table. Supported: {EsriUnits.DescribeSupported()}.");
        }

        return code;
    }

    private static double ResolveUnitFactor(int code, CoordinateReference? bufferCrs, CrsKind kind)
    {
        var angular = EsriUnits.IsAngular(code);
        if (!angular && kind == CrsKind.Projected && EsriUnits.TryGetLinear(code, out _, out var metres))
        {
            // Every projected CRS in the curated catalogue is metre-based.
            return metres;
        }

        if (angular && kind == CrsKind.Geographic && EsriUnits.TryGetAngular(code, out _, out var degrees))
        {
            return degrees;
        }

        if (!angular)
        {
            throw GeoServicesErrors.Invalid(
                $"Unit code {code} is linear but the buffer CRS {bufferCrs} is {kind}: " +
                "the planar engine cannot buffer metres in degrees (no geodesic verb). Name a projected 'bufferSR'.");
        }

        throw GeoServicesErrors.Invalid(
            $"Unit code {code} is angular but the buffer CRS {bufferCrs} is {kind}: " +
            "name a geographic 'bufferSR' or a linear unit with a projected 'bufferSR'.");
    }

    /// <summary>
    /// The datum-transformation search (10.x <c>findTransformations</c>). The
    /// candidates come from the provider's transformation graph (ADR-0074), so
    /// the response is a ranked list of real operations — each with its steps,
    /// the Helmert parameters it applies, the area it is valid over and its
    /// stated accuracy — rather than one composite naming a path. A pair on
    /// one datum returns the empty list: there is nothing to apply.
    /// </summary>
    private static IResult FindTransformations(EsriRequestParameters parameters, GeometryServiceCapabilities capabilities, CancellationToken cancellationToken)
    {
        var source = EsriValueParser.ParseSpatialReference(parameters.Require("inSR"))
            ?? throw GeoServicesErrors.Invalid("'inSR' must be a spatial reference (a WKID or {wkid} object).");
        var target = EsriValueParser.ParseSpatialReference(parameters.Require("outSR"))
            ?? throw GeoServicesErrors.Invalid("'outSR' must be a spatial reference (a WKID or {wkid} object).");
        RejectVerticalSearch(parameters);
        var count = ParseTransformationCount(parameters);
        var areaOfInterest = ParseAreaOfInterest(parameters, capabilities, source, cancellationToken);
        var candidates = SearchTransformations(source, target, capabilities, areaOfInterest, cancellationToken);
        var entries = candidates
            .Select(TransformationEntry.From)
            .ToArray();
        return EsriJson.Value(count == -1 ? entries : entries.Take(Math.Max(count, 0)).ToArray());
    }

    /// <summary>
    /// The candidates the graph returns, or a typed failure for a CRS the
    /// provider does not serve — the search never answers with an empty list
    /// for a reference it cannot read, because "no transformation needed" and
    /// "no such CRS" are different answers.
    /// </summary>
    private static IReadOnlyList<CrsTransformation> SearchTransformations(
        CoordinateReference? source,
        CoordinateReference target,
        GeometryServiceCapabilities capabilities,
        CrsAreaOfUse? areaOfInterest,
        CancellationToken cancellationToken) =>
        capabilities.Catalogue.FindTransformations(
            new CrsTransformationQuery(source?.ToString() ?? target.ToString(), target.ToString(), areaOfInterest),
            cancellationToken);

    /// <summary>
    /// <c>extentOfInterest</c> filters the candidates whose area of use
    /// covers it, so a search over ground no datum step is published for comes
    /// back with what is left rather than a refusal. The extent is in the
    /// source CRS's own coordinates, as Esri clients send it: a projected
    /// inSR brings metres, and the engine reprojects the two corners to the
    /// geographic box the catalogue records areas of use in.
    /// </summary>
    private static CrsAreaOfUse? ParseAreaOfInterest(
        EsriRequestParameters parameters,
        GeometryServiceCapabilities capabilities,
        CoordinateReference source,
        CancellationToken cancellationToken)
    {
        var raw = parameters.Get("extentOfInterest");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var (xMin, yMin, xMax, yMax) = ParseExtent(raw);
        var kind = capabilities.Catalogue.Describe(source.ToString(), cancellationToken).Kind;
        if (kind == CrsKind.Geographic)
        {
            return new CrsAreaOfUse("the requested extent of interest", xMin, yMin, xMax, yMax);
        }

        var (west, south) = ToGeographic(xMin, yMin, source, capabilities.Transforms, cancellationToken);
        var (east, north) = ToGeographic(xMax, yMax, source, capabilities.Transforms, cancellationToken);
        return new CrsAreaOfUse(
            "the requested extent of interest",
            Math.Min(west, east),
            Math.Min(south, north),
            Math.Max(west, east),
            Math.Max(south, north));
    }

    /// <summary>
    /// The extent in either the comma syntax (<c>xmin,ymin,xmax,ymax</c>) or
    /// the JSON envelope clients paste from a map extent.
    /// </summary>
    private static (double XMin, double YMin, double XMax, double YMax) ParseExtent(string raw)
    {
        if (raw.TrimStart().StartsWith('{'))
        {
            // The JSON envelope a client pastes off a map, read by the
            // shared codec rather than by a second envelope parser.
            var envelope = EsriValueParser.ParseGeometry(raw, null).Envelope
                ?? throw GeoServicesErrors.Invalid($"'extentOfInterest' must be 'xmin,ymin,xmax,ymax', got '{raw}'.");
            return (envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY);
        }

        var values = EsriValueParser.ParseDoubles(raw, "extentOfInterest");
        if (values.Count != 4)
        {
            throw GeoServicesErrors.Invalid(
                $"'extentOfInterest' must be 'xmin,ymin,xmax,ymax' (four numbers), got '{raw}'.");
        }

        return (values[0], values[1], values[2], values[3]);
    }

    private static (double Lon, double Lat) ToGeographic(
        double x,
        double y,
        CoordinateReference source,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var corner = GeometryFactory.CreatePoint(x, y, source);
        var transformed = transforms.Transform(corner, source.ToString(), "EPSG:4326", cancellationToken);
        var point = (Point)transformed;
        return (point.X!.Value, point.Y!.Value);
    }

    /// <summary>
    /// <c>vertical=false</c> — the default every client sends — is accepted:
    /// the catalogue carries horizontal CRSs only, so a horizontal search is
    /// what it asks for either way. <c>vertical=true</c> asks for vertical
    /// transformations, which the catalogue does not carry, and stays refused
    /// by name.
    /// </summary>
    private static void RejectVerticalSearch(EsriRequestParameters parameters)
    {
        if (parameters.GetBool("vertical", false))
        {
            throw GeoServicesErrors.Invalid(
                "The 'vertical' parameter is not supported: the curated catalogue carries horizontal CRSs only, so no vertical transformation can be found.");
        }
    }

    /// <summary>
    /// How many candidates to return: <c>-1</c> for all, otherwise a
    /// non-negative count. Both spellings clients use are read —
    /// <c>numOfResults</c> from the spec and <c>numTransformations</c> from
    /// the ArcGIS REST JS client.
    /// </summary>
    private static int ParseTransformationCount(EsriRequestParameters parameters)
    {
        var name = parameters.Has("numOfResults") ? "numOfResults" : "numTransformations";
        if (!parameters.Has(name))
        {
            return -1;
        }

        var raw = parameters.Get(name);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < -1)
        {
            throw GeoServicesErrors.Invalid($"'{name}' must be -1 (all) or a non-negative count, got '{raw}'.");
        }

        return count;
    }

    private static IResult Intersect(EsriRequestParameters parameters, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        var spatialReference = EsriValueParser.ParseSpatialReference(parameters.Get("sr"));
        var single = EsriValueParser.ParseGeometry(parameters.Require("geometry"), spatialReference);
        var geometries = EsriValueParser.ParseGeometries(parameters.Require("geometries"), spatialReference);
        return Geometries(geometries.Select(geometry => operations.Intersection(geometry, single, cancellationToken)));
    }

    private static IResult AreasAndLengths(EsriRequestParameters parameters, IGeometryMeasures measures, CancellationToken cancellationToken)
    {
        // Docs-verbatim input names (T-083): areasAndLengths names the array
        // 'polygons' (older docs/samples 'polys'); both map leniently to the
        // existing geometry parameter. Anything non-planar stays an honest
        // reject: the engine has no geodesic verb.
        var geometries = InputGeometries(parameters, "polygons", "polys");
        RequirePlanarCalculation(parameters);
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("areas");
            WriteNumbers(writer, geometries.Select(geometry => measures.Area(geometry, cancellationToken)));
            writer.WritePropertyName("lengths");
            WriteNumbers(writer, geometries.Select(geometry => measures.Length(geometry, cancellationToken)));
            writer.WriteEndObject();
        });
    }

    private static IResult Lengths(EsriRequestParameters parameters, IGeometryMeasures measures, CancellationToken cancellationToken)
    {
        // Docs-verbatim input name (T-083): lengths names the array
        // 'polylines'; it maps leniently to the existing geometry parameter.
        var geometries = InputGeometries(parameters, "polylines");
        RequirePlanarCalculation(parameters);
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("lengths");
            WriteNumbers(writer, geometries.Select(geometry => measures.Length(geometry, cancellationToken)));
            writer.WriteEndObject();
        });
    }

    private static IResult Distance(EsriRequestParameters parameters, IGeometryMeasures measures, CancellationToken cancellationToken)
    {
        var spatialReference = EsriValueParser.ParseSpatialReference(parameters.Get("sr"));
        var from = EsriValueParser.ParseGeometry(parameters.Require("geometry1"), spatialReference);
        var to = EsriValueParser.ParseGeometry(parameters.Require("geometry2"), spatialReference);
        return EsriJson.Value(new DistanceResponse(measures.Distance(from, to, cancellationToken)));
    }

    private static IResult ConvexHull(EsriRequestParameters parameters, IGeometryProcessing processing, CancellationToken cancellationToken) =>
        Geometries([processing.ConvexHull(InputGeometries(parameters), cancellationToken)]);

    private static IResult Difference(EsriRequestParameters parameters, IGeometryProcessing processing, CancellationToken cancellationToken)
    {
        var spatialReference = EsriValueParser.ParseSpatialReference(parameters.Get("sr"));
        var geometries = EsriValueParser.ParseGeometries(parameters.Require("geometries"), spatialReference);
        var subtract = EsriValueParser.ParseGeometry(parameters.Require("geometry"), spatialReference);
        return Geometries(geometries.Select(geometry => processing.Difference(geometry, subtract, cancellationToken)));
    }

    private static IResult Union(EsriRequestParameters parameters, IGeometryProcessing processing, CancellationToken cancellationToken) =>
        Geometries([processing.Union(InputGeometries(parameters), cancellationToken)]);

    private static IResult Simplify(EsriRequestParameters parameters, IGeometryProcessing processing, CancellationToken cancellationToken) =>
        Geometries(InputGeometries(parameters).Select(geometry => processing.Repair(geometry, cancellationToken)));

    private static IResult Relation(EsriRequestParameters parameters, IGeometryRelations relations, CancellationToken cancellationToken)
    {
        // Esri-docs verbatim (parity playground): geometries1/geometries2
        // with sr1/sr2 (modern docs use one shared 'sr') and a named
        // 'relation'. The legacy single-pair names ('geometries' vs one
        // 'geometry' with a DE-9IM 'relationParam') keep working.
        var spatialReference = EsriValueParser.ParseSpatialReference(parameters.Get("sr"))
            ?? EsriValueParser.ParseSpatialReference(parameters.Get("sr1"))
            ?? EsriValueParser.ParseSpatialReference(parameters.Get("sr2"));
        RejectMismatchedRelationReferences(parameters);
        var geometries = RelationInputs(parameters, "geometries", "geometries1", spatialReference);
        var others = RelationInputs(parameters, "geometry", "geometries2", spatialReference);
        var predicate = RelationPredicate(parameters);
        var results = geometries
            .Select(geometry => others.Any(other => predicate(geometry, other, relations, cancellationToken)) ? 1 : 0)
            .ToArray();
        return EsriJson.Value(new RelationResponse(results));
    }

    private static List<IGeometry> RelationInputs(
        EsriRequestParameters parameters, string primary, string alias, CoordinateReference? fallback)
    {
        var raw = parameters.Get(primary);
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = parameters.Get(alias);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            throw GeoServicesErrors.Invalid(
                $"The '{primary}' parameter is required (alias '{alias}' is also accepted).");
        }

        return EsriValueParser.ParseGeometries(raw, fallback);
    }

    private static void RejectMismatchedRelationReferences(EsriRequestParameters parameters)
    {
        var first = parameters.Get("sr1");
        var second = parameters.Get("sr2");
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return;
        }

        if (!string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw GeoServicesErrors.Invalid(
                "The 'sr1'/'sr2' spatial references differ: project both arrays to one shared reference first.");
        }
    }

    /// <summary>
    /// Resolves the relation test: a DE-9IM <c>relationParam</c> (plain or
    /// <c>RELATE(G1, G2, 'pattern')</c>) answers through the engine relate
    /// verb, and the named <c>relation</c> values with an exact DE-9IM
    /// equivalent map to it (intersects via negated disjoint). Dimension-
    /// dependent names stay an honest reject naming the supported set.
    /// </summary>
    private static Func<IGeometry, IGeometry, IGeometryRelations, CancellationToken, bool> RelationPredicate(
        EsriRequestParameters parameters)
    {
        var named = parameters.Get("relation");
        var custom = parameters.Get("relationParam");
        if (string.IsNullOrWhiteSpace(named))
        {
            // Legacy callers name only the DE-9IM pattern.
            var pattern = string.IsNullOrWhiteSpace(custom)
                ? throw GeoServicesErrors.Invalid("The 'relation' parameter is required ('relationParam' holds a DE-9IM pattern).")
                : ParseRelationPattern(custom);
            return (left, right, relations, token) => relations.Relate(left, right, pattern, token);
        }

        var relation = named.Trim();
        if (IsRelationPattern(relation))
        {
            return (left, right, relations, token) => relations.Relate(left, right, relation, token);
        }

        if (string.Equals(relation, "esriSpatialRelRelation", StringComparison.OrdinalIgnoreCase)
            || string.Equals(relation, "esriGeometryRelationRelation", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = string.IsNullOrWhiteSpace(custom)
                ? throw GeoServicesErrors.Invalid("The 'relationParam' parameter is required when 'relation' is a custom relation.")
                : ParseRelationPattern(custom);
            return (left, right, relations, token) => relations.Relate(left, right, pattern, token);
        }

        if (string.Equals(relation, "esriSpatialRelIntersects", StringComparison.OrdinalIgnoreCase))
        {
            return (left, right, relations, token) => !relations.Relate(left, right, "FF*FF****", token);
        }

        if (string.Equals(relation, "esriSpatialRelDisjoint", StringComparison.OrdinalIgnoreCase))
        {
            return (left, right, relations, token) => relations.Relate(left, right, "FF*FF****", token);
        }

        if (string.Equals(relation, "esriSpatialRelContains", StringComparison.OrdinalIgnoreCase))
        {
            return (left, right, relations, token) => relations.Relate(left, right, "T*****FF*", token);
        }

        if (string.Equals(relation, "esriSpatialRelWithin", StringComparison.OrdinalIgnoreCase))
        {
            return (left, right, relations, token) => relations.Relate(left, right, "T*F**F***", token);
        }

        if (string.Equals(relation, "esriSpatialRelEquals", StringComparison.OrdinalIgnoreCase))
        {
            return (left, right, relations, token) => relations.Relate(left, right, "T*F**FFF*", token);
        }

        throw GeoServicesErrors.Invalid(
            $"The 'relation' value '{named}' is not supported: the engine tests DE-9IM patterns " +
            "(esriSpatialRelIntersects/Disjoint/Contains/Within/Equals, or esriSpatialRelRelation with a 'relationParam' pattern).");
    }

    private static bool IsRelationPattern(string value) =>
        value.Length == 9 && value.All(character => character is 'T' or 'F' or '*' or '0');

    /// <summary>Unwraps the Shape Comparison Language form <c>RELATE(G1, G2, 'pattern')</c> to its pattern.</summary>
    private static string ParseRelationPattern(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("RELATE(", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        var firstQuote = trimmed.IndexOfAny(['\'', '"']);
        var lastQuote = trimmed.LastIndexOfAny(['\'', '"']);
        if (firstQuote < 0 || lastQuote <= firstQuote)
        {
            throw GeoServicesErrors.Invalid(
                $"The 'relationParam' value '{value}' is not a DE-9IM pattern or RELATE(G1, G2, 'pattern').");
        }

        return trimmed.Substring(firstQuote + 1, lastQuote - firstQuote - 1);
    }

    private static IResult Densify(EsriRequestParameters parameters, IGeometryProcessing processing, CancellationToken cancellationToken)
    {
        var maxSegmentLength = ParseDouble(parameters.Require("maxSegmentLength"), "maxSegmentLength");
        return Geometries(InputGeometries(parameters).Select(geometry => processing.Densify(geometry, maxSegmentLength, cancellationToken)));
    }

    private static IResult LabelPoints(EsriRequestParameters parameters, IGeometryMeasures measures, CancellationToken cancellationToken) =>
        Geometries(InputGeometries(parameters, "polygons", "polys").Select(geometry => measures.LabelPoint(geometry, cancellationToken)));

    private static List<IGeometry> InputGeometries(EsriRequestParameters parameters, params string[] aliases)
    {
        // The geometry-array input, accepting the docs-verbatim aliases
        // leniently: 'geometries' wins when present, otherwise the first
        // supplied alias. Aliasing renames the parameter — the value parses
        // through the unchanged geometry path, never reinterpreted.
        var spatialReference = EsriValueParser.ParseSpatialReference(parameters.Get("sr"));
        var raw = parameters.Get("geometries");
        foreach (var alias in aliases)
        {
            if (!string.IsNullOrWhiteSpace(raw))
            {
                break;
            }

            raw = parameters.Get(alias);
        }

        if (!string.IsNullOrWhiteSpace(raw))
        {
            return EsriValueParser.ParseGeometries(raw, spatialReference);
        }

        throw GeoServicesErrors.Invalid(aliases.Length == 0
            ? "The 'geometries' parameter is required."
            : $"The 'geometries' parameter is required (aliases '{string.Join("', '", aliases)}' are also accepted).");
    }

    /// <summary>
    /// The engine measures planar (no geodesic verb): <c>calculationType</c>
    /// absent or <c>planar</c> answers directly; anything else
    /// (<c>geodesic</c>, <c>preserveShape</c>) fails honestly rather than
    /// answering planar silently.
    /// </summary>
    private static void RequirePlanarCalculation(EsriRequestParameters parameters)
    {
        var raw = parameters.Get("calculationType");
        if (string.IsNullOrWhiteSpace(raw)
            || string.Equals(raw.Trim(), "planar", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw GeoServicesErrors.Invalid(
            $"The 'calculationType' value '{raw}' is not supported: the engine measures planar (no geodesic verb); omit 'calculationType' or pass 'planar'.");
    }

    private static IResult Geometries(IEnumerable<IGeometry> geometries) =>
        EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("geometries");
            writer.WriteStartArray();
            foreach (var geometry in geometries)
            {
                EsriGeometryCodec.Write(writer, geometry);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private static void WriteNumbers(Utf8JsonWriter writer, IEnumerable<double> values)
    {
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteNumberValue(value);
        }

        writer.WriteEndArray();
    }

    private static void Reject(EsriRequestParameters parameters, string name, string message)
    {
        if (parameters.Has(name))
        {
            throw GeoServicesErrors.Invalid($"The '{name}' parameter is not supported: {message}");
        }
    }

    private static double ParseDouble(string value, string name) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : throw GeoServicesErrors.Invalid($"'{name}' must be a finite number, got '{value}'.");

    private static int ParseOptionalInt(EsriRequestParameters parameters, string name, int fallback) =>
        int.TryParse(parameters.Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}

/// <summary>The engine verbs the Geometry Service maps onto (ADR-0035 §6).</summary>
internal sealed record GeometryServiceCapabilities(
    IGeometryOperations Operations,
    IGeometryMeasures Measures,
    IGeometryProcessing Processing,
    IGeometryRelations Relations,
    ICoordinateTransforms Transforms,
    ICrsDirectory Catalogue);

/// <summary>The Geometry Service resource shape.</summary>
internal sealed record GeometryServerInfo(double CurrentVersion, string ServiceDescription, string Capabilities);

/// <summary>The <c>distance</c> operation result.</summary>
internal sealed record DistanceResponse(double Distance);

/// <summary>The <c>relation</c> operation result (one 1/0 per input geometry).</summary>
internal sealed record RelationResponse(IReadOnlyList<int> Relations);

/// <summary>One candidate from a <c>findTransformations</c> listing: the operation's name, its steps, the accuracy the catalogue states for it and where it is valid (spec §7.0.9).</summary>
internal sealed record TransformationEntry(
    string Name,
    IReadOnlyList<TransformationStep> GeoTransforms,
    double Accuracy,
    bool Approximate,
    TransformationAreaOfUse AreaOfUse)
{
    public static TransformationEntry From(CrsTransformation candidate) =>
        new(
            candidate.Name,
            candidate.Steps.Select(TransformationStep.From).ToArray(),
            candidate.AccuracyMetres,
            candidate.Approximate,
            new TransformationAreaOfUse(
                candidate.AreaOfUse.Name,
                candidate.AreaOfUse.XMin,
                candidate.AreaOfUse.YMin,
                candidate.AreaOfUse.XMax,
                candidate.AreaOfUse.YMax));
}

/// <summary>One step of a candidate: the operation, the direction it runs in, and the parameters it applies.</summary>
internal sealed record TransformationStep(string Name, bool TransformForward, string Method, TransformationHelmert Helmert)
{
    public static TransformationStep From(CrsTransformationStep step) =>
        new(
            step.Name,
            step.TransformForward,
            step.Method,
            new TransformationHelmert(
                step.Parameters.Tx,
                step.Parameters.Ty,
                step.Parameters.Tz,
                step.Parameters.Rx,
                step.Parameters.Ry,
                step.Parameters.Rz,
                step.Parameters.ScalePpm));
}

/// <summary>The seven parameters of a Helmert step: metres, arc-seconds, parts per million (EPSG method 9606).</summary>
internal sealed record TransformationHelmert(double Tx, double Ty, double Tz, double Rx, double Ry, double Rz, double Scale);

/// <summary>Where a candidate is valid, in the degrees EPSG records extents in. The member names are the Esri envelope names (<c>xmin</c>), not camel-cased capitals.</summary>
internal sealed record TransformationAreaOfUse(
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("xmin")] double XMin,
    [property: System.Text.Json.Serialization.JsonPropertyName("ymin")] double YMin,
    [property: System.Text.Json.Serialization.JsonPropertyName("xmax")] double XMax,
    [property: System.Text.Json.Serialization.JsonPropertyName("ymax")] double YMax);
