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
/// <see cref="IGeometryRelations"/>. The semantic trap is explicit: both
/// <c>generalize</c> and <c>simplify</c> are Douglas-Peucker generalization
/// and take the same engine verb under their own parameter names
/// (<c>maxDeviation</c> and <c>deviation</c>/<c>value</c> respectively).
/// Topological repair is <see cref="IGeometryProcessing.Repair"/> and has no
/// Esri operation name, so nothing maps to it — mapping it to
/// <c>simplify</c> would silently generalize nothing and repair instead.
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
            ["simplify"] = (parameters, capabilities, token) => Simplify(parameters, capabilities.Operations, token),
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

    /// <summary>
    /// Accepts a <c>datumTransformation</c> that names the operation project
    /// applies, and refuses any other by naming that one: a client that asked
    /// for a different path deserves to know which path it got, not a result
    /// computed by a transformation it did not choose.
    /// </summary>
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

        var available = applied is null
            ? "the catalogue lists no datum transformation for this pair"
            : $"project applies '{applied.Name}'";
        throw GeoServicesErrors.Invalid(
            $"The 'datumTransformation' parameter names '{named}', which project does not apply: {available}. " +
            "The catalogue carries Helmert operations only, so no alternative is projected in its place; " +
            "call findTransformations for the ranked candidates and their accuracies.");
    }

    private static IResult Generalize(EsriRequestParameters parameters, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        // Spec §7.0.13: generalize generalizes by `maxDeviation`, the maximum
        // allowable deviation in the units of the spatial reference.
        var spatialReference = EsriValueParser.ParseSpatialReference(parameters.Get("sr"));
        var deviation = ParseDouble(parameters.Require("maxDeviation"), "maxDeviation");
        var geometries = EsriValueParser.ParseGeometries(parameters.Require("geometries"), spatialReference);
        return Geometries(geometries.Select(geometry => operations.Simplify(geometry, deviation, cancellationToken)));
    }

    private static IResult Buffer(EsriRequestParameters parameters, GeometryServiceCapabilities capabilities, CancellationToken cancellationToken)
    {
        // Spec §7.0.6: buffered in bufferSR ?? outSR ?? inSR, returned in
        // outSR ?? bufferSR ?? inSR. Two distances are possible meanings for
        // the same request and both are served, because the engine has the
        // verbs for them (ADR-0075):
        //  - a linear unit against a geographic buffer CRS is a ground
        //    distance, so it goes through IGeodesicBuffering (the reproject-
        //    and-buffer within the tolerance that verb states);
        //  - everything else is the planar IGeometryOperations.Buffer, in
        //    the buffer CRS, reached through ICoordinateTransforms.
        var fallback = EsriValueParser.ParseSpatialReference(parameters.Get("inSR"))
            ?? EsriValueParser.ParseSpatialReference(parameters.Get("sr"));
        var bufferSr = EsriValueParser.ParseSpatialReference(parameters.Get("bufferSR"));
        var outSr = EsriValueParser.ParseSpatialReference(parameters.Get("outSR"));
        // A CRS-less input is interpreted in the buffer CRS, so parse it with
        // that stamp: the ground-distance verb reads the reference off the
        // geometry, exactly as the planar path resolves it.
        var geometries = EsriValueParser.ParseGeometries(parameters.Require("geometries"), fallback ?? bufferSr ?? outSr);
        var work = new BufferWork(
            geometries,
            fallback,
            bufferSr,
            outSr,
            EsriValueParser.ParseDoubles(parameters.Require("distances"), "distances"),
            ParseOptionalInt(parameters, "quadrantSegments", 8),
            ParseUnitCode(parameters),
            parameters.GetBool("geodesic", false),
            parameters.GetBool("unionResults", false));
        if (work.Distances.Count != 1 && work.Distances.Count != work.Geometries.Count)
        {
            throw GeoServicesErrors.Invalid("'distances' must hold one value or one value per input geometry.");
        }

        var results = new IGeometry[work.Geometries.Count];
        for (var i = 0; i < work.Geometries.Count; i++)
        {
            results[i] = BufferOne(work, i, capabilities, cancellationToken);
        }

        return work.UnionResults
            ? Geometries([UnionResults(results, capabilities, cancellationToken)])
            : Geometries(results);
    }

    /// <summary>
    /// <c>unionResults=true</c> dissolves the per-input buffers into one
    /// geometry, so the result array holds a single member. The dissolve is
    /// only meaningful in one CRS, so inputs that resolve to different
    /// references are refused by name.
    /// </summary>
    private static IGeometry UnionResults(IGeometry[] results, GeometryServiceCapabilities capabilities, CancellationToken cancellationToken)
    {
        var reference = results[0].CoordinateReference;
        foreach (var result in results.Skip(1))
        {
            if (result.CoordinateReference != reference)
            {
                throw GeoServicesErrors.Invalid(
                    "'unionResults' needs every input in one spatial reference: the inputs resolve to different references. Name a shared 'outSR'.");
            }
        }

        return capabilities.Processing.Union([.. results], cancellationToken);
    }

    /// <summary>The <c>unit</c> code, or <c>null</c> when distances are already in buffer-CRS units.</summary>
    private static int? ParseUnitCode(EsriRequestParameters parameters)
    {
        var raw = parameters.Get("unit");
        return string.IsNullOrWhiteSpace(raw) ? null : ParseUnitCode(raw);
    }

    private sealed record BufferWork(
        IReadOnlyList<IGeometry> Geometries,
        CoordinateReference? Fallback,
        CoordinateReference? BufferSr,
        CoordinateReference? OutSr,
        IReadOnlyList<double> Distances,
        int Segments,
        int? Unit,
        bool Geodesic,
        bool UnionResults)
    {
        public double Distance(int index) => Distances.Count == 1 ? Distances[0] : Distances[index];
    }

    private static IGeometry BufferOne(BufferWork work, int index, GeometryServiceCapabilities capabilities, CancellationToken cancellationToken)
    {
        // CRS-less inputs are interpreted in the buffer CRS, as before.
        var source = BufferSource(work, index);
        var bufferCrs = work.BufferSr ?? work.OutSr ?? source;
        var kind = BufferKind(capabilities.Catalogue, bufferCrs, cancellationToken);
        var distance = work.Distance(index);

        // A linear distance against a geographic buffer CRS is a ground
        // distance: metres along the earth, not degrees on a plane. That is
        // the request the old facade rejected by name.
        if (work.Unit is { } code && !EsriUnits.IsAngular(code) && kind == CrsKind.Geographic)
        {
            EsriUnits.TryGetLinear(code, out _, out var metresPerUnit);
            var buffered = GeodesicBuffer(capabilities, work.Geometries[index], distance * metresPerUnit, work.Segments, cancellationToken);
            var geodesicTarget = BufferTarget(work, source);
            return DiffersFrom(geodesicTarget, source)
                ? capabilities.Transforms.Transform(buffered, source?.ToString(), geodesicTarget!.Value.ToString(), cancellationToken)
                : buffered;
        }

        RejectGeodesic(work, bufferCrs, kind);
        var planarDistance = distance * PlanarDistanceFactor(work, bufferCrs, kind);
        var working = DiffersFrom(bufferCrs, source)
            ? capabilities.Transforms.Transform(work.Geometries[index], source?.ToString(), bufferCrs!.Value.ToString(), cancellationToken)
            : work.Geometries[index];
        var expanded = capabilities.Operations.Buffer(working, planarDistance, work.Segments, cancellationToken);
        var target = BufferTarget(work, source);
        return DiffersFrom(target, bufferCrs)
            ? capabilities.Transforms.Transform(expanded, bufferCrs?.ToString(), target!.Value.ToString(), cancellationToken)
            : expanded;
    }

    /// <summary>
    /// The ground-distance verb, called through the adapter's choke point so
    /// a structured engine failure becomes the Esri invalid-parameters code
    /// with the reason intact (ADR-0035).
    /// </summary>
    private static IGeometry GeodesicBuffer(
        GeometryServiceCapabilities capabilities,
        IGeometry geometry,
        double distanceMetres,
        int segments,
        CancellationToken cancellationToken)
    {
        try
        {
            return capabilities.GeodesicBuffers.Buffer(geometry, distanceMetres, segments, cancellationToken);
        }
        catch (SpatialException exception)
        {
            throw GeoServicesErrors.Invalid(exception.Message, exception);
        }
    }

    /// <summary>
    /// <c>geodesic</c> is served where a ground distance is what the request
    /// means. It is refused elsewhere rather than answered planar, so a
    /// caller never gets a planar answer for a request that asked for a
    /// geodesic one.
    /// </summary>
    private static void RejectGeodesic(BufferWork work, CoordinateReference? bufferCrs, CrsKind? kind)
    {
        if (!work.Geodesic)
        {
            return;
        }

        if (kind == CrsKind.Projected)
        {
            throw GeoServicesErrors.Invalid(
                $"The 'geodesic' parameter is not supported against the projected buffer CRS {bufferCrs}: ground-distance buffering is served for a geographic buffer CRS; drop 'geodesic', or drop the projected 'bufferSR' and name a linear 'unit'.");
        }

        throw GeoServicesErrors.Invalid(
            "The 'geodesic' parameter needs a linear 'unit' against a geographic buffer CRS: the engine buffers ground distances in metres, not degrees. "
            + (work.Unit is null
                ? "Name a linear 'unit' (for example 9001 for metres) or drop 'geodesic' to buffer the distances as degrees."
                : $"Unit code {work.Unit} is angular; name a linear one, or drop 'geodesic' to buffer the distances as degrees."));
    }

    private static CoordinateReference? BufferTarget(BufferWork work, CoordinateReference? source) =>
        work.OutSr ?? work.BufferSr ?? source;

    private static CoordinateReference? BufferSource(BufferWork work, int index) =>
        work.Geometries[index].CoordinateReference ?? work.Fallback ?? work.BufferSr ?? work.OutSr;

    private static CrsKind? BufferKind(ICrsDirectory catalogue, CoordinateReference? bufferCrs, CancellationToken cancellationToken) =>
        bufferCrs is null ? null : catalogue.Describe(bufferCrs.Value.ToString(), cancellationToken).Kind;

    private static bool DiffersFrom(CoordinateReference? left, CoordinateReference? right) =>
        left.HasValue && left.Value != right;

    /// <summary>
    /// Converts one raw <c>distances</c> value into buffer-CRS units. Without
    /// <c>unit</c> the distances are already in buffer-CRS units (metres for
    /// the catalogue's projected CRSs, degrees for geographic ones); with a
    /// linear <c>unit</c> the buffer CRS must be projected (metres), with an
    /// angular <c>unit</c> it must be geographic (degrees). A linear unit
    /// against a geographic CRS never reaches here — it is a ground distance
    /// and goes to <see cref="GeodesicBuffer"/>.
    /// </summary>
    private static double PlanarDistanceFactor(BufferWork work, CoordinateReference? bufferCrs, CrsKind? kind)
    {
        if (work.Unit is not { } code)
        {
            return 1.0;
        }

        if (bufferCrs is null)
        {
            throw GeoServicesErrors.Invalid("'unit' requires a spatial reference: name 'bufferSR' (or 'inSR'/'sr') so distances have units.");
        }

        return EsriUnitCode.Factor(code, bufferCrs.Value, kind ?? CrsKind.Geographic);
    }

    /// <summary>
    /// Parses the <c>unit</c> parameter to a curated Esri unit code, rejecting
    /// non-numeric codes and codes outside the curated linear/angular tables.
    /// </summary>
    internal static int ParseUnitCode(string raw) => EsriUnitCode.Parse(raw, "unit");

    private static string Describe(CrsKind? kind) => kind?.ToString().ToLowerInvariant() ?? "unclassified";

    /// <summary>
    /// The datum-transformation search (10.x <c>findTransformations</c>). The
    /// candidates come from the provider's transformation graph (ADR-0087), so
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
        var entries = SearchTransformations(source, target, capabilities, areaOfInterest, cancellationToken)
            .Select(TransformationEntry.From)
            .ToArray();
        return EsriJson.Value(count == -1 ? entries : entries.Take(Math.Max(count, 0)).ToArray());
    }

    /// <summary>
    /// The candidates the graph returns. A CRS the provider does not serve
    /// fails here rather than answering with an empty list, because "no
    /// transformation needed" and "no such CRS" are different answers.
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
    /// <c>extentOfInterest</c> filters the candidates whose area of use covers
    /// it, so a search over ground no datum step is published for comes back
    /// with what is left rather than a refusal. The extent is in the source
    /// CRS's own coordinates, as Esri clients send it: a projected inSR brings
    /// metres, and the engine reprojects the two corners onto the geographic
    /// box the catalogue records areas of use in.
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
        if (capabilities.Catalogue.Describe(source.ToString(), cancellationToken).Kind == CrsKind.Geographic)
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
            var envelope = EsriValueParser.ParseGeometry(raw, null).Envelope
                ?? throw GeoServicesErrors.Invalid($"'extentOfInterest' must be 'xmin,ymin,xmax,ymax', got '{raw}'.");
            return (envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY);
        }

        var values = EsriValueParser.ParseDoubles(raw, "extentOfInterest");
        return values.Count == 4
            ? (values[0], values[1], values[2], values[3])
            : throw GeoServicesErrors.Invalid(
                $"'extentOfInterest' must be 'xmin,ymin,xmax,ymax' (four numbers), got '{raw}'.");
    }

    /// <summary>
    /// One corner of the requested extent, in the geographic degrees the
    /// catalogue records its areas of use in.
    /// </summary>
    private static (double Lon, double Lat) ToGeographic(
        double x,
        double y,
        CoordinateReference source,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var corner = GeometryFactory.CreatePoint(x, y, source);
        var point = (Point)transforms.Transform(corner, source.ToString(), "EPSG:4326", cancellationToken);
        return (point.X!.Value, point.Y!.Value);
    }

    /// <summary>
    /// <c>vertical=false</c> — the default every client sends — is accepted:
    /// the catalogue carries horizontal CRSs only, so a horizontal search is
    /// what it is asked for either way. <c>vertical=true</c> asks for vertical
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
    /// the ArcGIS REST JS client. The default is every ranked candidate,
    /// because a search that hides its second-best answer behind a default is
    /// not a search.
    /// </summary>
    private static int ParseTransformationCount(EsriRequestParameters parameters)
    {
        var name = parameters.Has("numOfResults") ? "numOfResults" : "numTransformations";
        if (!parameters.Has(name))
        {
            return -1;
        }

        var raw = parameters.Get(name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count >= -1
            ? count
            : throw GeoServicesErrors.Invalid($"'{name}' must be -1 (all) or a non-negative count, got '{raw}'.");
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

    /// <summary>
    /// Spec §7.0.5: <c>simplify</c> is generalization, not topological repair.
    /// The tolerance is <c>deviation</c> — the maximum allowable deviation in
    /// the units of the spatial reference — or, mutually exclusively,
    /// <c>value</c>, the same quantity for a client that names it that way.
    /// One of the two is required, so a request that carries neither fails by
    /// name instead of generalizing with no tolerance at all.
    /// </summary>
    private static IResult Simplify(EsriRequestParameters parameters, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        // The tolerance is validated before the payload, so a request that is
        // wrong in both ways names the parameter it is most likely to fix.
        var tolerance = SimplifyTolerance(parameters);
        var spatialReference = EsriValueParser.ParseSpatialReference(parameters.Get("sr"));
        var geometries = EsriValueParser.ParseGeometries(parameters.Require("geometries"), spatialReference);
        return Geometries(geometries.Select(geometry => operations.Simplify(geometry, tolerance, cancellationToken)));
    }

    /// <summary>The one tolerance <c>simplify</c> accepts, or a typed failure naming what is wrong with it.</summary>
    private static double SimplifyTolerance(EsriRequestParameters parameters)
    {
        var deviation = OptionalTolerance(parameters, "deviation");
        var value = OptionalTolerance(parameters, "value");
        return (deviation, value) switch
        {
            (not null, not null) => throw GeoServicesErrors.Invalid(
                "The 'deviation' and 'value' parameters are mutually exclusive; send one tolerance."),
            (not null, null) => deviation.Value,
            (null, not null) => value.Value,
            _ => throw GeoServicesErrors.Invalid(
                "The 'deviation' parameter is required for simplify; 'value' is the alternative spelling of the same tolerance."),
        };
    }

    /// <summary>
    /// A tolerance parameter, or <c>null</c> when it was not sent. A value that
    /// is present but unusable fails here, naming the parameter, rather than
    /// reaching the engine.
    /// </summary>
    private static double? OptionalTolerance(EsriRequestParameters parameters, string name)
    {
        var raw = parameters.Get(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var tolerance = ParseDouble(raw, name);
        return tolerance >= 0
            ? tolerance
            : throw GeoServicesErrors.Invalid($"'{name}' must be non-negative, got '{raw}'.");
    }

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

        // The dimension-aware verbs are read out of the one pattern table the
        // feature query path tests with, so the same verb has one answer
        // whichever endpoint serves it (SpatialEngine-zpz). Intersects joins
        // them there for the same reason (SpatialEngine-51k): both endpoints
        // answer it from the OGC intersect patterns.
        if (TryNamedRelation(relation, out var dimensionAware))
        {
            return dimensionAware;
        }

        if (string.Equals(relation, "esriSpatialRelEquals", StringComparison.OrdinalIgnoreCase))
        {
            return (left, right, relations, token) => relations.Relate(left, right, "T*F**FFF*", token);
        }

        throw GeoServicesErrors.Invalid(
            $"The 'relation' value '{named}' is not supported: the engine tests DE-9IM patterns " +
            "(esriSpatialRelIntersects/Disjoint/Contains/Within/Touches/Overlaps/Crosses/Equals, or esriSpatialRelRelation with a 'relationParam' pattern).");
    }

    /// <summary>
    /// The named predicate for the relations read out of
    /// <see cref="SpatialRelationPredicates"/> — the one pattern table the
    /// feature query path tests with (ADR-0036), so there is no second copy of
    /// a pattern string to drift. That covers the dimension-dependent verbs
    /// and, since SpatialEngine-51k, <c>Intersects</c>: both endpoints answer
    /// it from the OGC intersect patterns rather than from a built
    /// intersection. The left geometry plays the feature and the right the
    /// query, the roles the query path gives them (SpatialEngine-2ve owns
    /// whether that is the direction the protocol wants for every verb).
    /// </summary>
    private static bool TryNamedRelation(
        string relation, out Func<IGeometry, IGeometry, IGeometryRelations, CancellationToken, bool> predicate)
    {
        Func<GeometryPair, IGeometryRelations, CancellationToken, bool>? matched = null;
        if (Matches(relation, "esriSpatialRelTouches"))
        {
            matched = SpatialRelationPredicates.Touches;
        }
        else if (Matches(relation, "esriSpatialRelOverlaps"))
        {
            matched = SpatialRelationPredicates.Overlaps;
        }
        else if (Matches(relation, "esriSpatialRelCrosses"))
        {
            matched = SpatialRelationPredicates.Crosses;
        }
        else if (Matches(relation, "esriSpatialRelIntersects"))
        {
            matched = SpatialRelationPredicates.Intersects;
        }

        predicate = (left, right, relations, token) =>
            matched is not null
            && GeometryPair.Of(left, right) is { } pair
            && matched(pair, relations, token);
        return matched is not null;
    }

    private static bool Matches(string relation, string name) =>
        string.Equals(relation, name, StringComparison.OrdinalIgnoreCase);

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
    ICrsDirectory Catalogue,
    IGeodesicBuffering GeodesicBuffers);

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
