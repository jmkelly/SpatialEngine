using System.Globalization;
using System.Text.Json;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Transformations;

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
            ["project"] = (parameters, capabilities, token) => Project(parameters, capabilities.Transforms, token),
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
            ["findtransformations"] = (parameters, capabilities, token) => FindTransformations(parameters, capabilities.Catalogue, token),
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

    private static IResult Project(EsriRequestParameters parameters, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        // The engine has no datum tables: reject a client-supplied datum
        // transformation by name (like the query path) rather than project
        // silently without it. Clients needing a datum step should consult
        // findTransformations for the curated catalogue path.
        Reject(parameters, "datumTransformation", "datum transformations are not supported; reprojection uses the registered transforms.");
        var source = EsriValueParser.ParseSpatialReference(parameters.Get("inSR"));
        var target = EsriValueParser.ParseSpatialReference(parameters.Require("outSR"))
            ?? throw GeoServicesErrors.Invalid("'outSR' is required for project.");
        var geometries = EsriValueParser.ParseGeometries(parameters.Require("geometries"), source);
        var results = geometries
            .Select(geometry => transforms.Transform(geometry, source?.ToString(), target.ToString(), cancellationToken))
            .ToArray();
        return Geometries(results);
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
    /// The datum-transformation lookup (10.x <c>findTransformations</c>): an
    /// honest listing of the curated catalogue path, not the Esri WKID
    /// registry. Same underlying datum → the empty list (no transformation
    /// needed); a datum step → one forward composite naming the Helmert
    /// path, with the OSGB36 classic-Helmert note. The response is the bare
    /// JSON array clients paste into <c>project</c> as-is.
    /// </summary>
    private static IResult FindTransformations(EsriRequestParameters parameters, ICrsDirectory catalogue, CancellationToken cancellationToken)
    {
        var source = EsriValueParser.ParseSpatialReference(parameters.Require("inSR"))
            ?? throw GeoServicesErrors.Invalid("'inSR' must be a spatial reference (a WKID or {wkid} object).");
        var target = EsriValueParser.ParseSpatialReference(parameters.Require("outSR"))
            ?? throw GeoServicesErrors.Invalid("'outSR' must be a spatial reference (a WKID or {wkid} object).");
        RejectUnsupportedTransformationOptions(parameters);
        var count = ParseTransformationCount(parameters);
        var from = catalogue.Describe(source!.ToString(), cancellationToken);
        var to = catalogue.Describe(target!.ToString(), cancellationToken);
        if (source == target || SameDatum(from.Datum, to.Datum))
        {
            return EsriJson.Value(Array.Empty<TransformationEntry>());
        }

        return EsriJson.Value(SliceTransformations(TransformationEntries(from.Datum, to.Datum), count));
    }

    private static void RejectUnsupportedTransformationOptions(EsriRequestParameters parameters)
    {
        if (parameters.GetBool("vertical", false))
        {
            throw GeoServicesErrors.Invalid("The 'vertical' parameter is not supported: the curated catalogue carries horizontal CRSs only.");
        }

        if (!string.IsNullOrWhiteSpace(parameters.Get("extentOfInterest")))
        {
            throw GeoServicesErrors.Invalid("The 'extentOfInterest' parameter is not supported: the catalogue has no area-of-use model to rank transformations; omit it.");
        }
    }

    private static int ParseTransformationCount(EsriRequestParameters parameters)
    {
        var count = ParseOptionalInt(parameters, "numOfResults", 1);
        return count < -1
            ? throw GeoServicesErrors.Invalid($"'numOfResults' must be -1 (all) or a non-negative count, got '{parameters.Get("numOfResults")}'.")
            : count;
    }

    private static bool SameDatum(string? from, string? to) =>
        from is not null && string.Equals(from, to, StringComparison.OrdinalIgnoreCase);

    private static TransformationEntry[] TransformationEntries(string? from, string? to) =>
        [
            new([new TransformationStep($"{DatumName(from)}_To_{DatumName(to)}_Helmert", true, TransformationMethod(from, to))]),
        ];

    private static string TransformationMethod(string? from, string? to) =>
        "Helmert datum shift on the transform path via the WGS 84 pivot (embedded TOWGS84 parameters; zero for modern datums)."
            + (IsOrdnanceSurvey(from) || IsOrdnanceSurvey(to)
                ? " OSGB36 uses the classic Helmert approximation: no OSTN grid support, metre-level accuracy."
                : string.Empty);

    private static TransformationEntry[] SliceTransformations(TransformationEntry[] entries, int count) =>
        count == -1 ? entries : entries.Take(Math.Max(count, 0)).ToArray();

    private static bool IsOrdnanceSurvey(string? datum) =>
        datum?.Contains("Ordnance Survey", StringComparison.OrdinalIgnoreCase) == true;

    private static string DatumName(string? datum)
    {
        var name = string.IsNullOrWhiteSpace(datum) ? "unknown datum" : datum.Trim();
        var builder = new System.Text.StringBuilder(name.Length);
        var underscore = false;
        foreach (var rune in name)
        {
            if (char.IsLetterOrDigit(rune))
            {
                builder.Append(rune);
                underscore = false;
            }
            else if (!underscore)
            {
                builder.Append('_');
                underscore = true;
            }
        }

        return builder.ToString().Trim('_');
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
        var spatialReference = EsriValueParser.ParseSpatialReference(parameters.Get("sr"));
        var geometries = EsriValueParser.ParseGeometries(parameters.Require("geometries"), spatialReference);
        var other = EsriValueParser.ParseGeometry(parameters.Require("geometry"), spatialReference);
        var pattern = parameters.Require("relationParam");
        var results = geometries.Select(geometry => relations.Relate(geometry, other, pattern, cancellationToken) ? 1 : 0).ToArray();
        return EsriJson.Value(new RelationResponse(results));
    }

    private static IResult Densify(EsriRequestParameters parameters, IGeometryProcessing processing, CancellationToken cancellationToken)
    {
        var maxSegmentLength = ParseDouble(parameters.Require("maxSegmentLength"), "maxSegmentLength");
        return Geometries(InputGeometries(parameters).Select(geometry => processing.Densify(geometry, maxSegmentLength, cancellationToken)));
    }

    private static IResult LabelPoints(EsriRequestParameters parameters, IGeometryMeasures measures, CancellationToken cancellationToken) =>
        Geometries(InputGeometries(parameters).Select(geometry => measures.LabelPoint(geometry, cancellationToken)));

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

/// <summary>One forward step of a <c>findTransformations</c> listing.</summary>
internal sealed record TransformationStep(string Name, bool TransformForward, string Method);

/// <summary>One forward composite of a <c>findTransformations</c> listing.</summary>
internal sealed record TransformationEntry(IReadOnlyList<TransformationStep> GeoTransforms);
