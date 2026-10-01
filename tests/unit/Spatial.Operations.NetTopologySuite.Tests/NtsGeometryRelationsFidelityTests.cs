using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite.Adapters;
using Nts = NetTopologySuite.Geometries;

namespace Spatial.Operations.NetTopologySuite.Tests;

/// <summary>
/// The fidelity of the DE-9IM verb (ADR-0036) against the reference
/// implementation, over the point, line and area combinations the Esri
/// surfaces serve — a characterisation, not a change (SpatialEngine-1dg).
///
/// The bead reported two things about this verb, and neither survives
/// contact with NetTopologySuite 2.6 on the served combinations. Both are
/// pinned here with what the implementation actually does, because "the
/// reference does not do that" is only worth having next to "here is what it
/// does do".
///
/// <list type="number">
/// <item><description><b>The rendered matrix.</b> The bead read
/// <c>Geometry.Relate(other).ToString()</c> as a point on a polygon boundary
/// rendering <c>F0FFFF212</c> and a point inside a polygon rendering
/// <c>0FFFFF212</c>, against hand-computed <c>FTFFFFFFT</c> and
/// <c>0FFTFFTTT</c>, and concluded that the provider does not render the
/// canonical cell order. For the served fixtures it renders the canonical
/// order: the unit square against a point on its boundary renders
/// <c>FF20F1FF2</c> and against a point in its interior <c>0F2FF1FF2</c>,
/// cell for cell what the matrix is
/// (<see cref="The_rendered_matrix_is_the_hand_computed_matrix"/>), and the
/// rendered string is the transpose of the reverse pair's for every ordered
/// pair (<see cref="Every_pair_read_in_both_operand_orders_is_its_own_transpose"/>).
/// Neither reported string is the matrix of either pair — the hand-computed
/// columns of the report are not matrices either, since a point's own
/// boundary is empty and so every cell in its boundary row is
/// <c>F</c> whatever the other geometry does. What the report was reading is
/// most likely the raw <see cref="GeometryAdapter"/> geometry rather than a
/// relate call, but the conclusion the engine can act on is the one in the
/// first test: the verb's answers are the matrix's answers.</description></item>
/// <item><description><b>The typed predicates.</b> The bead reported the
/// reference's own named predicates disagreeing with the OGC alternation —
/// <c>IsCrosses(Point, Curve)</c> false for a point on a line's interior,
/// where <c>T*T******</c> says true, and <c>IsCrosses(Surface, Curve)</c>
/// false for a line through a polygon. The alternation patterns are
/// <b>dimension-keyed</b>: <c>T*T******</c> is the L/A reading and
/// <c>T**T*****</c> the A/L one, and neither is stated for a pair that
/// involves a point, so reading one of them over a point/line pair asks a
/// question the standard does not ask that pair. Asked the served question —
/// the dimension-keyed table the adapter serves —
/// the reference's named predicates and the served patterns agree on all six
/// verbs over all 256 ordered pairs of the served fixtures
/// (<see cref="The_served_table_agrees_with_the_references_named_predicate"/>).
/// The one place the reference does disagree with itself is
/// <c>Overlaps</c> and <c>Crosses</c> for a mixed-dimension pair, and that
/// is the gate ADR-0036 already records (SpatialEngine-u2x.56): a
/// crossing line against a polygon overlaps under a dimension-blind union of
/// the alternation, and reads as both, which is why the served table selects
/// one mask per dimension pair rather than unioning them.</description></item>
/// </list>
///
/// The consequence for the served surface is that no served predicate depends
/// on a deviation from the standard here. The one reading the engine does
/// inherit from the provider is already recorded in ADR-0036: the DE-9IM
/// vocabulary gap for a point on a line, where the matrix names a contact and
/// nothing else, so <c>Contains</c> and <c>Within</c> read false for a point
/// sitting on a line and that is the standard's answer rather than the
/// provider's.
/// </summary>
public sealed class NtsGeometryRelationsFidelityTests
{
    private readonly NtsGeometryRelations _relations = new();

    /// <summary>
    /// The served fixtures: the unit square the matrix table reads against,
    /// three more areas, five lines and four points. Every ordered pair is a
    /// combination the Esri surfaces can be asked — the Feature Service
    /// <c>spatialRel</c> predicates compare a feature geometry with a query
    /// geometry in both orders, so both orders are swept.
    /// </summary>
    private static readonly (string Name, Func<IGeometry> Build)[] Fixtures =
    [
        ("square-equal", () => Square(0, 0, 10, 10)),
        ("square-inner", () => Square(2, 2, 4, 4)),
        ("square-overlap", () => Square(5, 5, 15, 15)),
        ("square-above", () => Square(0, 10, 10, 20)),
        ("square-outside", () => Square(20, 20, 30, 30)),
        ("line-crossing", () => Line(0, 5, 20, 5)),
        ("line-through", () => Line(2, 5, 18, 5)),
        ("line-chord", () => Line(3, 5, 7, 5)),
        ("line-inside", () => Line(2, 2, 8, 8)),
        ("line-edge", () => Line(0, 0, 10, 0)),
        ("line-collinear", () => Line(-5, 0, 15, 0)),
        ("point-inside", () => Point(5, 5)),
        ("point-on-boundary", () => Point(0, 5)),
        ("point-vertex", () => Point(0, 0)),
        ("point-outside", () => Point(20, 20)),
    ];

    /// <summary>The fixture the hand computations below read against.</summary>
    private const string Feature = "square-equal";

    /// <summary>
    /// The six named relations the Esri surfaces serve, each the
    /// dimension-keyed mask table ADR-0036 records. Spelled out here rather
    /// than imported from the adapter: this is the reference reading, and
    /// importing the served constants would compare the served table with
    /// itself.
    /// </summary>
    private static readonly (string Name, string[] Masks)[] Relations =
    [
        ("contains", ["T*****FF*"]),
        ("within", ["T*F**F***"]),
        ("touches", ["FT*******", "F**T*****", "F***T****"]),
        ("overlaps", ["T*T***T**", "1*T***T**"]),
        ("crosses", ["T**T*****", "T*T******", "0********"]),
        ("intersects", ["T********", "*T*******", "***T*****", "****T****"]),
    ];

    /// <summary>
    /// Every hand-computed matrix, against the unit square read on the left.
    /// Each is derived from the definitions — interior, boundary and exterior
    /// on both sides, rows the left geometry's and columns the right's — and
    /// written here rather than read back from the provider, so the tests
    /// below cannot agree with a provider defect by construction.
    ///
    /// | Right-hand geometry | Matrix | Hand computation |
    /// | --- | --- | --- |
    /// | the square itself | <c>2FFF1FFF2</c> | the interiors share all of the interior (2), neither boundary reaches the other's exterior (both F), the boundaries coincide (1) and both exteriors are shared (2). |
    /// | square (2,2)-(4,4), inside | <c>212FF1FF2</c> | interiors meet in an area (2), the inner boundary is inside the square's interior (1) and its interior inside the square (2), the square's boundary touches neither (F) but does reach the inner square's exterior (1), and neither exterior reaches the other geometry (F). |
    /// | square (5,5)-(15,15), overlapping | <c>212101212</c> | the two boundaries <em>cross</em> at (10,5) and (5,10) without coinciding, so boundary∩boundary is two points (0); each boundary otherwise runs inside the other interior (1) and outside it (1), and both exteriors reach the other's interior (2). |
    /// | square (0,10)-(10,20), sharing an edge | <c>FF2F11212</c> | interiors disjoint (F), the shared edge is boundary∩boundary in dimension one (1), each boundary has the rest of itself outside the other (1), and both exteriors reach the other's interior (2). |
    /// | square (20,20)-(30,30), disjoint | <c>FF2FF1212</c> | nothing but exteriors: interior∩interior and the two boundary rows are empty (F), and every exterior cell is non-empty (2, 1, 2). |
    /// | line (0,5)-(20,5), one endpoint on the edge | <c>1F2001102</c> | the part of the line inside the square is a curve (1), the square's interior does not reach the line's boundary — (0,5) is on the square's <em>boundary</em> and (20,5) is outside it (F) — the square's interior reaches the line's exterior (2), the square's boundary meets the line's interior at both crossing points (0) and the line's boundary at (0,5) (0), and each boundary has the rest of itself outside the other (1). |
    /// | line (2,5)-(18,5), entering and leaving | <c>1020F1102</c> | as above, but the line's boundary is (2,5) inside the square's interior (0) and (18,5) outside it, and the square's boundary never reaches the line's boundary — neither endpoint lies on it (F). |
    /// | line (3,5)-(7,5), a chord inside | <c>102FF1FF2</c> | the whole line is inside the square, its interior is a curve (1) and its endpoints are inside the square's interior (0); the square's boundary meets neither (F, F) and reaches the line's exterior (1). |
    /// | line (0,0)-(10,0), along the bottom edge | <c>FF2101FF2</c> | the line lies wholly on the square's boundary, so interior∩interior is empty (F), the square's boundary reaches the line's interior (1) and its endpoints (0), the square's interior is nowhere on the line (F), the square's boundary reaches the line's exterior (1) and the line's interior reaches neither of the square's others (F, F). |
    /// | line (-5,0)-(15,0), the edge and past both ends | <c>FF21F1102</c> | as the previous row, except the square's boundary lies strictly inside the line and so does not reach the line's boundary (F), while the line's boundary is inside the square's exterior (1). |
    /// | line (2,2)-(8,8), inside | <c>102FF1FF2</c> | as the chord: identical, because "inside" is the whole statement. |
    /// | point (5,5), inside | <c>0F2FF1FF2</c> | the point is the interior∩interior (0), its own boundary is empty so the interior and boundary rows against it are empty (F, F), the square's interior reaches the point's exterior (2), the square's boundary does not reach the point (F, F) but does reach its exterior (1). |
    /// | point (0,5), on the boundary | <c>FF20F1FF2</c> | as the interior point, except the point is the square's boundary against its interior (0) rather than empty. |
    /// | point (0,0), on a vertex | <c>FF20F1FF2</c> | the same row: a vertex is boundary, and the DE-9IM says so identically for a vertex and for a point in the middle of an edge. |
    /// | point (20,20), outside | <c>FF2FF10F2</c> | as the interior point, except the point is the square's exterior against its interior (0). |
    /// </summary>
    public static TheoryData<string, string, string> HandComputedMatrices()
    {
        var rows = new TheoryData<string, string, string>();
        foreach (var (name, matrix) in new (string Name, string Matrix)[]
        {
            ("square-equal", "2FFF1FFF2"),
            ("square-inner", "212FF1FF2"),
            ("square-overlap", "212101212"),
            ("square-above", "FF2F11212"),
            ("square-outside", "FF2FF1212"),
            ("line-crossing", "1F2001102"),
            ("line-through", "1020F1102"),
            ("line-chord", "102FF1FF2"),
            ("line-edge", "FF2101FF2"),
            ("line-collinear", "FF21F1102"),
            ("line-inside", "102FF1FF2"),
            ("point-inside", "0F2FF1FF2"),
            ("point-on-boundary", "FF20F1FF2"),
            ("point-vertex", "FF20F1FF2"),
            ("point-outside", "FF2FF10F2"),
        })
        {
            rows.Add(Feature, name, matrix);
        }

        return rows;
    }

    /// <summary>Every ordered pair of the served fixtures.</summary>
    public static TheoryData<string, string> OrderedPairs()
    {
        var pairs = new TheoryData<string, string>();
        foreach (var (feature, _) in Fixtures)
        {
            foreach (var (query, _) in Fixtures)
            {
                pairs.Add(feature, query);
            }
        }

        return pairs;
    }

    /// <summary>Every ordered pair crossed with every served relation name.</summary>
    public static TheoryData<string, string, string> PairsByRelation()
    {
        var cases = new TheoryData<string, string, string>();
        foreach (var (feature, _) in Fixtures)
        {
            foreach (var (query, _) in Fixtures)
            {
                foreach (var (name, _) in Relations)
                {
                    cases.Add(feature, query, name);
                }
            }
        }

        return cases;
    }

    /// <summary>
    /// The bead's first observation, pinned as it measures out: the provider
    /// renders the canonical nine-cell matrix, cell for cell, over every served
    /// combination — so the verb that serves the Feature Service answers the
    /// matrix and not a rendering of it.
    /// </summary>
    [Theory]
    [MemberData(nameof(HandComputedMatrices))]
    public void The_rendered_matrix_is_the_hand_computed_matrix(string feature, string query, string matrix)
    {
        var rendered = GeometryAdapter.ToNts(Build(feature)).Relate(GeometryAdapter.ToNts(Build(query))).ToString();

        Assert.Equal(matrix, rendered);

        // And the matcher agrees with the rendering cell by cell, rather than
        // with a second computation of the same thing: every cell is asked on
        // its own, non-empty first and then by dimension.
        Assert.Equal(matrix, MatrixFor(Build(feature), Build(query)));
    }

    /// <summary>
    /// The other half of the same reading: a pair's matrix read in one operand
    /// order is the transpose of the matrix read in the other — rows and
    /// columns exchanged. The Feature Service compares a feature with a query
    /// in whichever order the caller wrote them, so a matrix that is not the
    /// transpose of its reverse is a pair whose two orders are two different
    /// geometries.
    /// </summary>
    [Theory]
    [MemberData(nameof(OrderedPairs))]
    public void Every_pair_read_in_both_operand_orders_is_its_own_transpose(string feature, string query)
    {
        var forward = MatrixFor(Build(feature), Build(query));
        var reverse = MatrixFor(Build(query), Build(feature));

        Assert.Equal(forward, Transpose(reverse));
    }

    /// <summary>
    /// The served pattern table read over each hand-computed matrix, cell by
    /// cell by a matcher written here — the served answers have to be the
    /// matrix's answers, and the matrix is the one in the table above rather
    /// than one read back out of the provider.
    /// </summary>
    [Theory]
    [MemberData(nameof(HandComputedMatrices))]
    public void Every_served_pattern_is_answered_as_the_hand_computed_matrix_reads(string feature, string query, string matrix)
    {
        var left = Build(feature);
        var right = Build(query);
        var token = CancellationToken.None;

        foreach (var (name, masks) in Relations)
        {
            var expected = Selected(name, Dimension(left), Dimension(right))
                .Any(mask => Reads(matrix, mask));

            Assert.Equal(expected, Any(left, right, Selected(name, Dimension(left), Dimension(right)), token));
        }
    }

    /// <summary>
    /// The bead's second observation, pinned as it measures out: the served
    /// table and the reference implementation's own named predicates agree on
    /// all six relations over every ordered pair of the served fixtures. This
    /// is the check the bead's report could not make — it compared the
    /// reference's predicates with a dimension-blind union of the alternation,
    /// which is not the served question — and it is why the served table is
    /// the *dimension-keyed* table rather than that union.
    /// </summary>
    [Theory]
    [MemberData(nameof(PairsByRelation))]
    public void The_served_table_agrees_with_the_references_named_predicate(string feature, string query, string relation)
    {
        var left = GeometryAdapter.ToNts(Build(feature));
        var right = GeometryAdapter.ToNts(Build(query));

        var served = Selected(relation, Dimension(left), Dimension(right))
            .Any(mask => left.Relate(right, mask));

        Assert.Equal(Reference(relation, left, right), served);
    }

    /// <summary>
    /// The two pairs the bead named, spelled out with the matrix each one
    /// actually has. The bead read a point on the boundary as
    /// <c>F0FFFF212</c> against <c>FTFFFFFFT</c>, and a point inside as
    /// <c>0FFFFF212</c> against <c>0FFTFFTTT</c>; the served matrix is
    /// <see cref="The_rendered_matrix_is_the_hand_computed_matrix"/>'s, and the
    /// reason the report's hand computation cannot be right is that a point's
    /// own boundary is empty: <c>FTFFFFFFT</c> claims the point's interior
    /// meets the polygon's interior and <c>0FFTFFTTT</c> claims the point's
    /// boundary meets it, and neither geometry has a boundary point to place
    /// there.
    /// </summary>
    [Fact]
    public void The_two_pairs_the_bead_named_read_as_the_matrix_says()
    {
        Assert.Equal("FF20F1FF2", MatrixFor(Square(0, 0, 10, 10), Point(0, 5)));
        Assert.Equal("0F2FF1FF2", MatrixFor(Square(0, 0, 10, 10), Point(5, 5)));
    }

    /// <summary>
    /// The reference implementation's own named predicates, asked the pair
    /// directly rather than through a pattern.
    /// </summary>
    private static bool Reference(string relation, Nts.Geometry left, Nts.Geometry right) => relation switch
    {
        "contains" => left.Contains(right),
        "within" => left.Within(right),
        "touches" => left.Touches(right),
        "overlaps" => left.Overlaps(right),
        "crosses" => left.Crosses(right),
        _ => left.Intersects(right),
    };

    /// <summary>
    /// The masks a relation is read with for a dimension pair, which is the
    /// served table's own rule (ADR-0036): the alternation is keyed on the
    /// pair's dimensions, so a relation the standard states for one pair only
    /// reads false at every other pair rather than asking a mask that pair was
    /// never given.
    /// </summary>
    private static string[] Selected(string relation, int left, int right) => relation switch
    {
        "overlaps" => (left, right) switch
        {
            (2, 2) => ["T*T***T**"],
            (1, 1) => ["1*T***T**"],
            _ => [],
        },
        "crosses" => (left, right) switch
        {
            (2, 1) => ["T**T*****"],
            (1, 2) => ["T*T******"],
            (1, 1) => ["0********"],
            _ => [],
        },
        _ => MasksOf(relation),
    };

    private static string[] MasksOf(string relation) =>
        Relations.First(masks => masks.Name == relation).Masks;

    /// <summary>Whether any of <paramref name="patterns"/> holds — none selected is false.</summary>
    private bool Any(IGeometry left, IGeometry right, string[] patterns, CancellationToken cancellationToken) =>
        patterns.Any(pattern => _relations.Relate(left, right, pattern, cancellationToken));

    /// <summary>The topological dimension: points 0, lines 1, areas 2.</summary>
    private static int Dimension(IGeometry geometry) => geometry.Type switch
    {
        GeometryType.Point or GeometryType.MultiPoint => 0,
        GeometryType.LineString or GeometryType.MultiLineString => 1,
        _ => 2,
    };

    private static int Dimension(Nts.Geometry geometry) => geometry switch
    {
        Nts.Point or Nts.MultiPoint => 0,
        Nts.LineString or Nts.MultiLineString => 1,
        _ => 2,
    };

    /// <summary>
    /// A pair's exact intersection matrix, reconstructed from nine
    /// single-cell questions — "is this cell non-empty?", and when it is "is
    /// it a point, a curve or an area?" — so each cell is answered on its own
    /// and the string is the matrix rather than a reading of one.
    /// </summary>
    private string MatrixFor(IGeometry left, IGeometry right)
    {
        var cells = new char[9];
        for (var position = 0; position < cells.Length; position++)
        {
            cells[position] = CellAt(left, right, position);
        }

        return new string(cells);
    }

    private char CellAt(IGeometry left, IGeometry right, int position)
    {
        if (!_relations.Relate(left, right, At(position, "T"), CancellationToken.None))
        {
            Assert.True(
                _relations.Relate(left, right, At(position, "F"), CancellationToken.None),
                $"cell {position} is neither empty nor non-empty");
            return 'F';
        }

        return Dimensions.Single(dimension => _relations.Relate(
            left, right, At(position, dimension.ToString()), CancellationToken.None));
    }

    /// <summary>The three cell dimensions, asked in that order.</summary>
    private static readonly char[] Dimensions = ['0', '1', '2'];

    /// <summary>The all-wildcard pattern with one cell replaced by <paramref name="symbol"/>.</summary>
    private static string At(int position, string symbol) =>
        "*********"[..position] + symbol + "*********"[(position + 1)..];

    /// <summary>Whether a pattern holds over a matrix, read from the matrix string alone.</summary>
    private static bool Reads(string matrix, string pattern) =>
        matrix.Zip(pattern, (cell, asked) => asked switch
        {
            '*' => true,
            'T' => cell != 'F',
            'F' => cell == 'F',
            _ => cell == asked,
        }).All(holds => holds);

    /// <summary>A matrix read in the other operand order: rows and columns exchanged.</summary>
    private static string Transpose(string matrix) =>
        string.Concat(Enumerable.Range(0, 3).SelectMany(row => Enumerable.Range(0, 3).Select(column => matrix[(column * 3) + row])));

    private static IGeometry Build(string name)
    {
        var fixture = Fixtures.First(candidate => candidate.Name == name);
        return fixture.Build();
    }

    private static Polygon Square(double minX, double minY, double maxX, double maxY) => GeometryFactory.CreatePolygon(
    [
        new Coordinate(minX, minY),
        new Coordinate(maxX, minY),
        new Coordinate(maxX, maxY),
        new Coordinate(minX, maxY),
        new Coordinate(minX, minY),
    ]);

    private static LineString Line(double x1, double y1, double x2, double y2) => GeometryFactory.CreateLineString(
    [
        new Coordinate(x1, y1),
        new Coordinate(x2, y2),
    ]);

    private static Point Point(double x, double y) => GeometryFactory.CreatePoint(x, y);
}
