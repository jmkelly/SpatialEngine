using Spatial.Adapter.GeoServices;
using Spatial.Core.Geometry;
using Nts = NetTopologySuite.Geometries;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// One fixture shape in the canonical ordinates every representation of the
/// matrix is built from: the unit square feature's polygon, a line or a point,
/// named the way the DE-9IM matrix table and both surfaces' tests name them.
/// </summary>
internal sealed record RelationFixture(
    string Name,
    FixtureShape Shape,
    IReadOnlyList<double> Ordinates)
{
    /// <summary>The fixture as the engine's own geometry value.</summary>
    public IGeometry Geometry => Shape switch
    {
        FixtureShape.Polygon => GeometryFactory.CreatePolygon(
        [
            new Coordinate(Ordinates[0], Ordinates[1]),
            new Coordinate(Ordinates[2], Ordinates[1]),
            new Coordinate(Ordinates[2], Ordinates[3]),
            new Coordinate(Ordinates[0], Ordinates[3]),
            new Coordinate(Ordinates[0], Ordinates[1]),
        ]),
        FixtureShape.Line => GeometryFactory.CreateLineString(
            [new Coordinate(Ordinates[0], Ordinates[1]), new Coordinate(Ordinates[2], Ordinates[3])]),
        _ => GeometryFactory.CreatePoint(Ordinates[0], Ordinates[1]),
    };

    /// <summary>
    /// The same fixture as the Esri JSON a <c>geometries1</c>/<c>geometries2</c>
    /// array element carries, so the Geometry Service is asked with the shape
    /// the fixture table names rather than a second, differently spelled one.
    /// </summary>
    public string EsriJson => Shape switch
    {
        FixtureShape.Polygon =>
            $$"""{"rings":[[[{{Ordinates[0]}},{{Ordinates[1]}}],[{{Ordinates[2]}},{{Ordinates[1]}}],[{{Ordinates[2]}},{{Ordinates[3]}}],[{{Ordinates[0]}},{{Ordinates[3]}}],[{{Ordinates[0]}},{{Ordinates[1]}}]]]}""",
        FixtureShape.Line =>
            $$"""{"paths":[[[{{Ordinates[0]}},{{Ordinates[1]}}],[{{Ordinates[2]}},{{Ordinates[3]}}]]]}""",
        _ => $$"""{"x":{{Ordinates[0]}},"y":{{Ordinates[1]}}}""",
    };

    /// <summary>
    /// The fixture as the reference implementation's own geometry value, so a
    /// cross-check asks that implementation's named predicates the question
    /// rather than a spelled-out copy of the patterns the engine serves.
    /// </summary>
    public Nts.Geometry ReferenceGeometry
    {
        get
        {
            var first = new Nts.Coordinate(Ordinates[0], Ordinates[1]);
            return Shape switch
            {
                FixtureShape.Polygon => Nts.GeometryFactory.Default.CreatePolygon(
                    [first, new Nts.Coordinate(Ordinates[2], Ordinates[1]), new Nts.Coordinate(Ordinates[2], Ordinates[3]), new Nts.Coordinate(Ordinates[0], Ordinates[3]), first]),
                FixtureShape.Line => Nts.GeometryFactory.Default.CreateLineString(
                    [first, new Nts.Coordinate(Ordinates[2], Ordinates[3])]),
                _ => Nts.GeometryFactory.Default.CreatePoint(first),
            };
        }
    }
}

/// <summary>The three fixture shapes: a polygon, a line or a point.</summary>
internal enum FixtureShape
{
    Polygon,
    Line,
    Point,
}

/// <summary>
/// The hand-computed DE-9IM matrix and the six named-relation verdicts for
/// one fixture row against the unit square feature.
/// </summary>
/// <param name="De9im">
/// The pair's exact intersection matrix, read with the feature on the left:
/// the rows are the feature's interior/boundary/exterior and the columns the
/// query's, so position 1 is interior∩interior, position 2
/// feature-boundary∩query-interior, position 4 feature-interior∩query-boundary
/// and position 5 boundary∩boundary. Every cell carries its dimension — <c>F</c>
/// for empty, <c>T</c>, <c>0</c>, <c>1</c> or <c>2</c> for a point, a curve or
/// an area — because the column is the matrix and not a display of it
/// (ADR-0165).
/// </param>
internal sealed record RelationVerdicts(
    string De9im,
    bool Contains,
    bool Within,
    bool Touches,
    bool Overlaps,
    bool Crosses,
    bool Intersects)
{
    /// <summary>
    /// The verdict for one served relation name, so one table answers both
    /// surfaces: the query path spells it <c>spatialRel</c>, the Geometry
    /// Service <c>relation</c>, and the verdict for the verb is the same one.
    /// </summary>
    public bool For(string spatialRel) => spatialRel switch
    {
        EsriFeatureQuery.Contains => Contains,
        EsriFeatureQuery.Within => Within,
        EsriFeatureQuery.Touches => Touches,
        EsriFeatureQuery.Overlaps => Overlaps,
        EsriFeatureQuery.Crosses => Crosses,
        _ => Intersects,
    };
}

/// <summary>
/// The one DE-9IM fixture table both spatial-relation surfaces are measured
/// against — the Feature Service query path
/// (<c>FeatureSpatialMatcher</c>) and the Geometry Service <c>relation</c>
/// operation (<c>GeometryService</c>). SpatialEngine-dih: the two surfaces
/// served the same named verb from two copies of a fixture list and two
/// copies of an expected column, so the Geometry Service's answers were
/// pinned by values copied across by hand and the two-point touch was never
/// pinned there at all. There is one fixture definition and one verdict table
/// here, both surfaces build their own representation of a fixture, and both
/// read the verdict from the same row.
///
/// The feature is the unit square (0,0)-(10,10) — <c>square-equal</c> — and
/// every row is a query geometry against it. The verdict columns are the
/// exact DE-9IM relations the engine serves (ADR-0036) read in the protocol's
/// frame: <c>spatialRel</c> names the feature's relation to the input
/// geometry, so <c>Contains</c> and <c>Within</c> are the query geometry's
/// OGC relations to the feature and their columns are the transpose of the
/// feature-frame reading (ADR-0171). <c>Touches</c> is the three OGC masks
/// as one union, <c>Overlaps</c> and <c>Crosses</c> are keyed on the pair's
/// dimension pair, and <c>Intersects</c> is the four-pattern union — all four
/// closed under transposition, so the frame does not move them.
///
/// | Query geometry | DE-9IM | Contains | Within | Touches | Overlaps | Crosses | Intersects |
/// | --- | --- | --- | --- | --- | --- | --- | --- |
/// | the square itself | <c>2FFF1FFF2</c> | T | T | F | F | F | T |
/// | square (2,2)-(4,4), inside | <c>212FF1FF2</c> | F | T | F | F | F | T |
/// | square (5,5)-(15,15), overlapping | <c>212101212</c> | F | F | F | T | F | T |
/// | square (0,0)-(4,4), sharing the corner and two edges | <c>212F11FF2</c> | F | T | F | F | F | T |
/// | square (0,10)-(10,20), sharing only an edge | <c>FF2F11212</c> | F | F | T | F | F | T |
/// | line (0,0)-(10,0), along the bottom edge | <c>FF2101FF2</c> | F | F | T | F | F | T |
/// | line (-5,0)-(15,0), the edge and past both ends | <c>FF21F1102</c> | F | F | T | F | F | T |
/// | line (5,0)-(20,0), along the edge and past it | <c>FF2101102</c> | F | F | T | F | F | T |
/// | line (0,5)-(20,5), crossing | <c>1F2001102</c> | F | F | F | F | T | T |
/// | line (2,2)-(8,8), inside | <c>102FF1FF2</c> | F | T | F | F | F | T |
/// | line (0,0)-(0,10), lying on the boundary | <c>FF2101FF2</c> | F | F | T | F | F | T |
/// | point (5,5), inside | <c>0F2FF1FF2</c> | F | T | F | F | F | T |
/// | point (0,5), on the boundary | <c>FF20F1FF2</c> | F | F | T | F | F | T |
/// | point (20,20), outside | <c>FF2FF10F2</c> | F | F | F | F | F | F |
/// | square (20,20)-(30,30), disjoint | <c>FF2FF1212</c> | F | F | F | F | F | F |
///
/// The three line rows along the bottom edge are the touch rows the envelope
/// approximation and the single <c>F***T****</c> mask each got wrong in a
/// different operand order: a line lying along the square's edge reaches the
/// square's interior-to-query-boundary position, so its interior meets the
/// query's boundary in dimension one rather than the two boundaries merely
/// meeting. The Geometry Service pinned none of them, which is the
/// reproduction for this bead. Their matrices are three different rows and
/// only one of them is the row the table used to print for all three, which
/// the digits now show at a glance:
/// <see cref="FeatureSpatialRelationTests"/> derives each one from the
/// geometry and is what caught the two that were not (ADR-0156).
/// </summary>
internal static class SpatialRelationMatrix
{
    /// <summary>Every fixture name, in the order the pair tables walk them.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "square-equal", "square-inner", "square-overlap", "square-corner", "square-above", "square-outside",
        "line-crossing", "line-inside", "line-on-boundary", "line-collinear", "line-edge",
        "line-shifted-collinear",
        "point-inside", "point-on-boundary", "point-vertex", "point-outside",
    ];

    /// <summary>Every fixture, by name.</summary>
    public static IReadOnlyDictionary<string, RelationFixture> Fixtures { get; } = new Dictionary<string, RelationFixture>
    {
        ["square-equal"] = Polygon("square-equal", 0, 0, 10, 10),
        ["square-inner"] = Polygon("square-inner", 2, 2, 4, 4),
        ["square-overlap"] = Polygon("square-overlap", 5, 5, 15, 15),
        ["square-corner"] = Polygon("square-corner", 0, 0, 4, 4),
        ["square-above"] = Polygon("square-above", 0, 10, 10, 20),
        ["square-outside"] = Polygon("square-outside", 20, 20, 30, 30),
        ["line-crossing"] = Line("line-crossing", 0, 5, 20, 5),
        ["line-inside"] = Line("line-inside", 2, 2, 8, 8),
        ["line-on-boundary"] = Line("line-on-boundary", 0, 0, 0, 10),
        ["line-collinear"] = Line("line-collinear", -5, 0, 15, 0),
        ["line-edge"] = Line("line-edge", 0, 0, 10, 0),
        ["line-shifted-collinear"] = Line("line-shifted-collinear", 5, 0, 20, 0),
        ["point-inside"] = Point("point-inside", 5, 5),
        ["point-on-boundary"] = Point("point-on-boundary", 0, 5),
        ["point-vertex"] = Point("point-vertex", 0, 0),
        ["point-outside"] = Point("point-outside", 20, 20),
    };

    /// <summary>
    /// The hand-computed verdicts, keyed by the query fixture's name. A row
    /// is here because its matrix is one of the ones the table above states;
    /// the fixtures the pairs tables walk but the matrix does not cover
    /// (<c>point-vertex</c>) carry no verdict and are matched against the
    /// reference implementation's own named predicates instead. The
    /// <see cref="De9im"/> column is the exact intersection matrix, cell for
    /// cell, and is compared outright against the matrix
    /// <c>FeatureSpatialRelationTests</c> derives from the geometry
    /// (ADR-0156, ADR-0165).
    /// </summary>
    public static IReadOnlyDictionary<string, RelationVerdicts> Verdicts { get; } =
        new Dictionary<string, RelationVerdicts>
        {
            ["square-equal"] = new("2FFF1FFF2", Contains: true, Within: true, Touches: false, Overlaps: false, Crosses: false, Intersects: true),
            ["square-inner"] = new("212FF1FF2", Contains: false, Within: true, Touches: false, Overlaps: false, Crosses: false, Intersects: true),
            ["square-overlap"] = new("212101212", Contains: false, Within: false, Touches: false, Overlaps: true, Crosses: false, Intersects: true),
            ["square-corner"] = new("212F11FF2", Contains: false, Within: true, Touches: false, Overlaps: false, Crosses: false, Intersects: true),
            ["square-above"] = new("FF2F11212", Contains: false, Within: false, Touches: true, Overlaps: false, Crosses: false, Intersects: true),
            ["line-edge"] = new("FF2101FF2", Contains: false, Within: false, Touches: true, Overlaps: false, Crosses: false, Intersects: true),
            ["line-collinear"] = new("FF21F1102", Contains: false, Within: false, Touches: true, Overlaps: false, Crosses: false, Intersects: true),
            ["line-shifted-collinear"] = new("FF2101102", Contains: false, Within: false, Touches: true, Overlaps: false, Crosses: false, Intersects: true),
            ["line-crossing"] = new("1F2001102", Contains: false, Within: false, Touches: false, Overlaps: false, Crosses: true, Intersects: true),
            ["line-inside"] = new("102FF1FF2", Contains: false, Within: true, Touches: false, Overlaps: false, Crosses: false, Intersects: true),
            ["line-on-boundary"] = new("FF2101FF2", Contains: false, Within: false, Touches: true, Overlaps: false, Crosses: false, Intersects: true),
            ["point-inside"] = new("0F2FF1FF2", Contains: false, Within: true, Touches: false, Overlaps: false, Crosses: false, Intersects: true),
            ["point-on-boundary"] = new("FF20F1FF2", Contains: false, Within: false, Touches: true, Overlaps: false, Crosses: false, Intersects: true),
            ["point-outside"] = new("FF2FF10F2", Contains: false, Within: false, Touches: false, Overlaps: false, Crosses: false, Intersects: false),
            ["square-outside"] = new("FF2FF1212", Contains: false, Within: false, Touches: false, Overlaps: false, Crosses: false, Intersects: false),
        };

    /// <summary>The fixture the matrix is read against: the unit square.</summary>
    public const string Feature = "square-equal";

    /// <summary>
    /// The served named relations, in the order the tables walk them — the
    /// six exact DE-9IM verbs both surfaces serve out of the one pattern
    /// table. <c>esriSpatialRelEnvelopeIntersects</c> is the spec's coarse
    /// default and is not a DE-9IM verb, so it is not in this set.
    /// </summary>
    public static IReadOnlyList<string> Relations { get; } =
    [
        EsriFeatureQuery.Contains,
        EsriFeatureQuery.Within,
        EsriFeatureQuery.Touches,
        EsriFeatureQuery.Overlaps,
        EsriFeatureQuery.Crosses,
        EsriFeatureQuery.Intersects,
    ];

    /// <summary>The fixture by name, or a named failure rather than a default.</summary>
    public static RelationFixture Of(string name) =>
        Fixtures.TryGetValue(name, out var fixture)
            ? fixture
            : throw new ArgumentOutOfRangeException(nameof(name), name, "unknown fixture geometry");

    /// <summary>Every matrix row as a theory case: the query fixture and its verdict.</summary>
    public static TheoryData<string, RelationVerdicts> MatrixRows()
    {
        var rows = new TheoryData<string, RelationVerdicts>();
        foreach (var (name, verdict) in Verdicts)
        {
            rows.Add(name, verdict);
        }

        return rows;
    }

    /// <summary>
    /// Every matrix row crossed with every served relation: the query fixture,
    /// the relation name and the verdict both surfaces must give it.
    /// </summary>
    public static TheoryData<string, string, bool> MatrixCases()
    {
        var cases = new TheoryData<string, string, bool>();
        foreach (var (name, verdict) in Verdicts)
        {
            foreach (var relation in Relations)
            {
                cases.Add(name, relation, verdict.For(relation));
            }
        }

        return cases;
    }

    /// <summary>Every ordered pair of the polygon, line and point fixtures.</summary>
    public static TheoryData<string, string> Pairs()
    {
        var pairs = new TheoryData<string, string>();
        foreach (var feature in Names)
        {
            foreach (var query in Names)
            {
                pairs.Add(feature, query);
            }
        }

        return pairs;
    }

    /// <summary>Every ordered pair crossed with every served relation.</summary>
    public static TheoryData<string, string, string> PairCases()
    {
        var cases = new TheoryData<string, string, string>();
        foreach (var feature in Names)
        {
            foreach (var query in Names)
            {
                foreach (var relation in Relations)
                {
                    cases.Add(feature, query, relation);
                }
            }
        }

        return cases;
    }

    private static RelationFixture Polygon(string name, double minX, double minY, double maxX, double maxY) =>
        new(name, FixtureShape.Polygon, [minX, minY, maxX, maxY]);

    private static RelationFixture Line(string name, double x1, double y1, double x2, double y2) =>
        new(name, FixtureShape.Line, [x1, y1, x2, y2]);

    private static RelationFixture Point(string name, double x, double y) =>
        new(name, FixtureShape.Point, [x, y]);
}