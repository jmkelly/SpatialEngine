using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Operations;

/// <summary>
/// The shared conformance examples of the Phase 6 operation contracts (plan
/// §9 "conformance examples", §18 "every provider of a standard capability
/// runs the same fixtures"). Each example is a named invocation with argument
/// values; a conforming provider must serve every example the same way, and
/// the conformance suite (tests/conformance) runs them against every
/// provider of the contract — in-process and across the worker boundary.
/// Argument values carry only core geometry types (ADR-0005).
/// </summary>
public static class GeometryOperationConformanceExamples
{
    public static IReadOnlyList<ConformanceExample> Buffer { get; } =
    [
        new ConformanceExample(
            "point-ring",
            "Buffering a point by a positive distance yields an enclosing polygon.",
            Arguments(GeometryOperationArguments.Geometry, GeometryFactory.CreatePoint(0, 0),
                      GeometryOperationArguments.Distance, 1.0)),

        new ConformanceExample(
            "erosion",
            "A negative distance erodes a polygon towards its interior.",
            Arguments(GeometryOperationArguments.Geometry, Square(-2, -2, 2, 2),
                      GeometryOperationArguments.Distance, -0.5)),

        new ConformanceExample(
            "empty-input",
            "Buffering an empty geometry yields an empty result.",
            Arguments(GeometryOperationArguments.Geometry, GeometryFactory.CreateEmptyPoint(),
                      GeometryOperationArguments.Distance, 1.0)),

        new ConformanceExample(
            "segment-count",
            "An explicit quadrant segment count is honoured.",
            Arguments(GeometryOperationArguments.Geometry, GeometryFactory.CreatePoint(0, 0),
                      GeometryOperationArguments.Distance, 1.0,
                      GeometryOperationArguments.QuadrantSegments, 12L)),

        new ConformanceExample(
            "missing-distance",
            "A buffer without a distance is an invalid input.",
            Arguments(GeometryOperationArguments.Geometry, GeometryFactory.CreatePoint(0, 0))),

        new ConformanceExample(
            "non-finite-distance",
            "A non-finite distance is an invalid input.",
            Arguments(GeometryOperationArguments.Geometry, GeometryFactory.CreatePoint(0, 0),
                      GeometryOperationArguments.Distance, double.NaN)),
    ];

    public static IReadOnlyList<ConformanceExample> Intersection { get; } =
    [
        new ConformanceExample(
            "overlapping-squares",
            "Intersecting overlapping squares yields their shared rectangle.",
            Arguments(GeometryOperationArguments.Left, Square(0, 0, 2, 2),
                      GeometryOperationArguments.Right, Square(1, 1, 3, 3))),

        new ConformanceExample(
            "disjoint-squares",
            "Intersecting disjoint squares yields an empty result.",
            Arguments(GeometryOperationArguments.Left, Square(0, 0, 1, 1),
                      GeometryOperationArguments.Right, Square(5, 5, 6, 6))),

        new ConformanceExample(
            "touching-squares",
            "Intersecting squares that share an edge yields the shared segment.",
            Arguments(GeometryOperationArguments.Left, Square(0, 0, 1, 1),
                      GeometryOperationArguments.Right, Square(1, 0, 2, 1))),

        new ConformanceExample(
            "empty-left",
            "Intersecting an empty geometry with a square yields an empty result.",
            Arguments(GeometryOperationArguments.Left, GeometryFactory.CreateEmptyPoint(),
                      GeometryOperationArguments.Right, Square(0, 0, 1, 1))),

        new ConformanceExample(
            "missing-right",
            "An intersection without the right geometry is an invalid input.",
            Arguments(GeometryOperationArguments.Left, Square(0, 0, 1, 1))),
    ];

    public static IReadOnlyList<ConformanceExample> Validate { get; } =
    [
        new ConformanceExample(
            "valid-square",
            "A closed, non-self-intersecting polygon is valid.",
            Arguments(GeometryOperationArguments.Geometry, Square(0, 0, 1, 1))),

        new ConformanceExample(
            "self-intersecting-bowtie",
            "A self-intersecting polygon is invalid.",
            Arguments(GeometryOperationArguments.Geometry, Bowtie())),

        new ConformanceExample(
            "valid-line",
            "A simple line string is valid.",
            Arguments(GeometryOperationArguments.Geometry, GeometryFactory.CreateLineString(
                [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(2, 0)]))),

        new ConformanceExample(
            "empty-input",
            "An empty geometry is valid.",
            Arguments(GeometryOperationArguments.Geometry, GeometryFactory.CreateEmptyPoint())),

        new ConformanceExample(
            "missing-geometry",
            "A validation without a geometry is an invalid input.",
            Arguments()),
    ];

    public static IReadOnlyList<ConformanceExample> Simplify { get; } =
    [
        new ConformanceExample(
            "collinear-line",
            "Simplifying a line with interpolated collinear points removes them.",
            Arguments(GeometryOperationArguments.Geometry, GeometryFactory.CreateLineString(
                [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(2, 2), new Coordinate(3, 3), new Coordinate(4, 4)]),
                      GeometryOperationArguments.Tolerance, 0.5)),

        new ConformanceExample(
            "zero-tolerance",
            "A zero tolerance returns the geometry unchanged.",
            Arguments(GeometryOperationArguments.Geometry, GeometryFactory.CreateLineString(
                [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(2, 0)]),
                      GeometryOperationArguments.Tolerance, 0.0)),

        new ConformanceExample(
            "empty-input",
            "Simplifying an empty geometry yields an empty result.",
            Arguments(GeometryOperationArguments.Geometry, GeometryFactory.CreateEmptyPoint(),
                      GeometryOperationArguments.Tolerance, 1.0)),

        new ConformanceExample(
            "negative-tolerance",
            "A negative tolerance is an invalid input.",
            Arguments(GeometryOperationArguments.Geometry, GeometryFactory.CreateLineString(
                [new Coordinate(0, 0), new Coordinate(1, 1)]),
                      GeometryOperationArguments.Tolerance, -0.5)),
    ];

    /// <summary>Builds the argument dictionary for one example.</summary>
    public static IReadOnlyDictionary<string, object?> Arguments(params object?[] pairs)
    {
        var arguments = new Dictionary<string, object?>(pairs.Length / 2);
        for (var i = 0; i < pairs.Length; i += 2)
        {
            arguments[(string)pairs[i]!] = pairs[i + 1];
        }

        return arguments;
    }

    /// <summary>A closed square polygon from the given corners.</summary>
    public static Polygon Square(double minX, double minY, double maxX, double maxY) =>
        GeometryFactory.CreatePolygon(
            [new Coordinate(minX, minY), new Coordinate(maxX, minY), new Coordinate(maxX, maxY), new Coordinate(minX, maxY), new Coordinate(minX, minY)]);

    /// <summary>A closed four-corner polygon whose edges cross — structurally invalid.</summary>
    public static Polygon Bowtie() =>
        GeometryFactory.CreatePolygon(
            [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(0, 1), new Coordinate(1, 0), new Coordinate(0, 0)]);
}
