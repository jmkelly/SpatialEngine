using System.Globalization;
using System.Text.Json;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;

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
            : throw EsriInteropException.Invalid($"The Geometry Service operation '{operation}' is not supported.");

    private static readonly Dictionary<string, Func<EsriRequestParameters, GeometryServiceCapabilities, CancellationToken, IResult>> Operations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["project"] = (parameters, capabilities, token) => Project(parameters, capabilities.Transforms, token),
            ["generalize"] = (parameters, capabilities, token) => Generalize(parameters, capabilities.Operations, token),
            ["buffer"] = (parameters, capabilities, token) => Buffer(parameters, capabilities.Operations, token),
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
        };

    /// <summary>The Geometry Service resource metadata (spec §7.0.1).</summary>
    public static IResult Info() =>
        EsriJson.Value(new GeometryServerInfo(
            CurrentVersion,
            "SpatialEngine Geometry Service",
            "Project,Generalize,Buffer,Intersect,AreasAndLengths,Lengths,Distance,ConvexHull,Difference,Union,Simplify,Relation,Densify,LabelPoints"));

    private static IResult Project(EsriRequestParameters parameters, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        var source = EsriValueParser.ParseSpatialReference(parameters.Get("inSR"));
        var target = EsriValueParser.ParseSpatialReference(parameters.Require("outSR"))
            ?? throw EsriInteropException.Invalid("'outSR' is required for project.");
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

    private static IResult Buffer(EsriRequestParameters parameters, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        Reject(parameters, "unit", "buffer units are not supported; buffer in the geometry's own linear CRS.");
        Reject(parameters, "geodesic", "geodesic buffering is not supported; the engine buffers planar.");
        Reject(parameters, "unionResults", "unionResults is not supported; the result is an array per input.");
        var spatialReference = EsriValueParser.ParseSpatialReference(parameters.Get("bufferSR")) ?? EsriValueParser.ParseSpatialReference(parameters.Get("sr"));
        var geometries = EsriValueParser.ParseGeometries(parameters.Require("geometries"), spatialReference);
        var distances = EsriValueParser.ParseDoubles(parameters.Require("distances"), "distances");
        if (distances.Count != 1 && distances.Count != geometries.Count)
        {
            throw EsriInteropException.Invalid("'distances' must hold one value or one value per input geometry.");
        }

        var segments = ParseOptionalInt(parameters, "quadrantSegments", 8);
        var results = new IGeometry[geometries.Count];
        for (var i = 0; i < geometries.Count; i++)
        {
            results[i] = operations.Buffer(geometries[i], distances.Count == 1 ? distances[0] : distances[i], segments, cancellationToken);
        }

        return Geometries(results);
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
        var geometries = InputGeometries(parameters);
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
        var geometries = InputGeometries(parameters);
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

    private static List<IGeometry> InputGeometries(EsriRequestParameters parameters)
    {
        var spatialReference = EsriValueParser.ParseSpatialReference(parameters.Get("sr"));
        return EsriValueParser.ParseGeometries(parameters.Require("geometries"), spatialReference);
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
            throw EsriInteropException.Invalid($"The '{name}' parameter is not supported: {message}");
        }
    }

    private static double ParseDouble(string value, string name) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : throw EsriInteropException.Invalid($"'{name}' must be a finite number, got '{value}'.");

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
    ICoordinateTransforms Transforms);

/// <summary>The Geometry Service resource shape.</summary>
internal sealed record GeometryServerInfo(double CurrentVersion, string ServiceDescription, string Capabilities);

/// <summary>The <c>distance</c> operation result.</summary>
internal sealed record DistanceResponse(double Distance);

/// <summary>The <c>relation</c> operation result (one 1/0 per input geometry).</summary>
internal sealed record RelationResponse(IReadOnlyList<int> Relations);
