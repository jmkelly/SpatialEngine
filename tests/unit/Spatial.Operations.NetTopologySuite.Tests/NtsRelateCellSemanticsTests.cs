using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite.Adapters;
using Nts = NetTopologySuite.Geometries;

namespace Spatial.Operations.NetTopologySuite.Tests;

/// <summary>
/// The cells the DE-9IM verb reports, characterised rather than changed
/// (SpatialEngine-aqy, ADR-0166). The bead that produced this file reported
/// three divergences between <c>Relate(a, b, pattern)</c> and
/// <c>Relate(a, b)</c> in NetTopologySuite 2.6, and none of the three is
/// what the report measured:
///
/// <list type="number">
/// <item><description><b>A 0-D operand has an empty boundary</b>, so a line
/// against a point at its endpoint reads <c>FF10F0FF2</c> — the contact is in
/// the line's <em>boundary</em> against the point's <em>interior</em>
/// (position 4), not empty, and the pair is not disjoint
/// (<see cref="A_line_and_a_point_at_its_endpoint_meet_in_the_lines_boundary_row"/>).
/// This is the JTS reading and the one PostGIS reports; it is stated here
/// because a reader coming to DE-9IM from the OGC glossary sometimes expects
/// the point's boundary to be the point.</description></item>
/// <item><description><b>Vertex-only contact is dimension zero</b>, not
/// <c>T</c> and not an edge: two squares meeting at a corner read
/// <c>FF2F01212</c> against the edge-sharing <c>FF2F11212</c>, one cell
/// apart in position 5 (<see cref="Two_areas_touching_at_a_vertex_read_a_point_where_they_share_an_edge_they_read_a_curve"/>).</description></item>
/// <item><description><b>The two paths are one computation.</b>
/// <c>Relate(g)</c> reaches the matrix through <c>GeometryRelate.RelateV1</c>
/// and <c>Relate(g, pattern)</c> through the same call, so a cell cannot be
/// reported one way by the rendering and another by the pattern: they are
/// equal cell for cell over every ordered pair of this file's fixtures
/// (<see cref="The_pattern_path_and_the_matrix_path_are_one_computation"/>).</description></item>
/// </list>
///
/// What is left is the cross-check the bead said the served surface could
/// not make — every pattern the query path serves, asked against the
/// provider's own named predicates — and it does find a real divergence:
/// <c>Crosses</c>. Not over the geometry, but over which reading of the OGC
/// alternation the two are speaking: the served mask answers
/// <see cref="The_served_table_diverges_from_the_reference_predicate_only_where_the_measurement_says"/>
/// for a line lying wholly inside an area and touching its boundary, and the
/// provider's predicate answers false. The measurement names every pair it
/// diverges on, so a divergence that moves is a failing test rather than a
/// served answer nobody compared.
/// </summary>
public sealed class NtsRelateCellSemanticsTests
{
    private readonly NtsGeometryRelations _relations = new();

    /// <summary>
    /// One fixture of every contact class the matrix has a cell for: an area
    /// met at a vertex, at an edge and in an overlap; a line crossing, wholly
    /// inside, touching the boundary from inside at an interior vertex,
    /// running along the boundary, and touching it at an endpoint; points on
    /// a vertex, on an edge and in the interior; and the multi-part spellings
    /// of the three, because the served table keys on the collection's
    /// dimension while the provider's predicates are component-wise.
    /// </summary>
    private static readonly (string Name, Func<IGeometry> Build)[] Fixtures =
    [
        ("square", () => Square(0, 0, 10, 10)),
        ("square-corner", () => Square(10, 10, 20, 20)),
        ("square-edge", () => Square(10, 0, 20, 10)),
        ("square-inner", () => Square(2, 2, 4, 4)),
        ("line-through", () => Line((-5, 5), (15, 5))),
        ("line-chord", () => Line((5, 2), (5, 8))),
        ("line-touch-vertex", () => Line((5, 2), (10, 5), (5, 8))),
        ("line-along-edge", () => Line((0, 0), (5, 0), (5, 5))),
        ("line-edge-span", () => Line((0, 0), (10, 0))),
        ("line-endpoint-on-edge", () => Line((0, 5), (5, 2), (5, 8))),
        ("point-vertex", () => Point(0, 0)),
        ("point-edge", () => Point(5, 0)),
        ("point-inside", () => Point(5, 5)),
        ("multiline-touch", () => GeometryFactory.CreateMultiLineString([Line((5, 2), (10, 5), (5, 8))])),
        ("multipolygon-single", () => GeometryFactory.CreateMultiPolygon([Square(0, 0, 10, 10)])),
        ("multipoint", () => GeometryFactory.CreateMultiPoint([Point(5, 5), Point(20, 20)])),
    ];

    /// <summary>
    /// The served verbs, each the dimension-keyed mask table ADR-0036 and
    /// ADR-0106 record — spelled out here rather than imported from the
    /// adapter, because importing the served constants would compare the
    /// served table with itself.
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
    /// Every pair the served <c>Crosses</c> reading answers differently from
    /// the provider's own <c>Crosses</c>, named <c>relation|feature|query</c>
    /// and carrying why. Sixteen pairs of the 256, in two classes:
    ///
    /// <list type="bullet">
    /// <item><description><b>The line lies wholly inside the closed area and
    /// its interior reaches the area's boundary</b> — position 4 is
    /// non-empty while position 7, the exterior against the line's interior,
    /// is empty. The served mask <c>T**T*****</c> asks position 1 and
    /// position 4 and answers true; the provider's predicate also requires
    /// the line to have left the area, and answers false. Five pairs.</description></item>
    /// <item><description><b>A 0-D operand is on one side.</b> The served
    /// table names no <c>Crosses</c> mask for a dimension pair involving a
    /// point and asks nothing, so it reads false (ADR-0106); the provider's
    /// predicate is component-wise over a <see cref="GeometryType.MultiPoint"/>
    /// and answers true when one of its points crosses the other geometry.
    /// Eleven pairs.</description></item>
    /// </list>
    ///
    /// The first class is a divergence between two readings of the same OGC
    /// row and is served as ADR-0106 states; the second is ADR-0106's own
    /// decision, measured here rather than re-opened. Both are the whole of
    /// what the two disagree on.
    /// </summary>
    private static readonly Dictionary<string, string> DocumentedDivergences = new()
    {
        // The line is inside the area and touches its boundary from inside.
        ["crosses|square|line-touch-vertex"] = "the line's interior reaches the area's boundary (position 4) without reaching its exterior (position 7)",
        ["crosses|square|line-along-edge"] = "the line's interior reaches the area's boundary (position 4) without reaching its exterior (position 7)",
        ["crosses|square|multiline-touch"] = "the line's interior reaches the area's boundary (position 4) without reaching its exterior (position 7)",
        ["crosses|multipolygon-single|line-touch-vertex"] = "the line's interior reaches the area's boundary (position 4) without reaching its exterior (position 7)",
        ["crosses|multipolygon-single|line-along-edge"] = "the line's interior reaches the area's boundary (position 4) without reaching its exterior (position 7)",
        ["crosses|multipolygon-single|multiline-touch"] = "the line's interior reaches the area's boundary (position 4) without reaching its exterior (position 7)",
        // A point is on one side, so the served table names no mask at all.
        ["crosses|square|multipoint"] = "a 0-D operand names no served Crosses mask",
        ["crosses|multipoint|square"] = "a 0-D operand names no served Crosses mask",
        ["crosses|multipolygon-single|multipoint"] = "a 0-D operand names no served Crosses mask",
        ["crosses|multipoint|multipolygon-single"] = "a 0-D operand names no served Crosses mask",
        ["crosses|line-through|multipoint"] = "a 0-D operand names no served Crosses mask",
        ["crosses|multipoint|line-through"] = "a 0-D operand names no served Crosses mask",
        ["crosses|line-chord|multipoint"] = "a 0-D operand names no served Crosses mask",
        ["crosses|multipoint|line-chord"] = "a 0-D operand names no served Crosses mask",
        ["crosses|line-endpoint-on-edge|multipoint"] = "a 0-D operand names no served Crosses mask",
        ["crosses|multipoint|line-endpoint-on-edge"] = "a 0-D operand names no served Crosses mask",
    };

    /// <summary>Every ordered pair of the fixtures.</summary>
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
    /// The bead's third report, pinned as it measures out: the two ways into
    /// the relation agree cell for cell. The matrix is read the way a pattern
    /// is answered — nine single-cell questions, each asked on its own through
    /// the verb under test — and the rendering is read from the provider, so
    /// the two are independent enough for the comparison to mean something.
    /// A wildcard is <c>*</c>, a non-empty cell is asked <c>T</c> and then by
    /// dimension, and a cell that is neither is <c>F</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(OrderedPairs))]
    public void The_pattern_path_and_the_matrix_path_are_one_computation(string feature, string query)
    {
        var left = Build(feature);
        var right = Build(query);

        var rendered = GeometryAdapter.ToNts(left).Relate(GeometryAdapter.ToNts(right)).ToString();

        Assert.Equal(rendered, MatrixFor(left, right));
    }

    /// <summary>
    /// The first divergence the bead reported, pinned as it measures out: a
    /// line (0,0)-(10,0) against a point on its endpoint is not disjoint. The
    /// point is the line's boundary against the point's interior — position 4
    /// — so the interiors are disjoint (position 1 <c>F</c>) while the pair
    /// still meets, and the served intersect union reads true. The report read
    /// "the point's boundary is empty" as a defect in the matrix; it is the
    /// reading JTS and PostGIS both report, and it is why the contact lands
    /// in position 4 rather than in either boundary cell.
    /// </summary>
    [Fact]
    public void A_line_and_a_point_at_its_endpoint_meet_in_the_lines_boundary_row()
    {
        var line = Line((0, 0), (10, 0));
        var endpoint = Point(0, 0);

        Assert.Equal("FF10F0FF2", MatrixFor(line, endpoint));
        Assert.Equal("F0FFFF102", MatrixFor(endpoint, line));

        // Position 4 is the contact, and the served intersect union names it.
        Assert.True(_relations.Relate(line, endpoint, "***T*****", CancellationToken.None));
        Assert.True(_relations.Relate(endpoint, line, "*T*******", CancellationToken.None));
    }

    /// <summary>
    /// The same reading stated as a property rather than as a pair: a 0-D
    /// geometry contributes no boundary, so wherever one is an operand every
    /// cell of its own boundary row or column is empty — positions 4, 5 and 6
    /// for a point on the left, positions 2, 5 and 8 for a point on the
    /// right. A meeting with a point can therefore only ever be reported
    /// through the other geometry's interior or boundary, which is what puts a
    /// point on a line's endpoint into the line's boundary row rather than
    /// into either interior.
    /// </summary>
    [Theory]
    [MemberData(nameof(OrderedPairs))]
    public void A_point_operand_contributes_no_boundary(string feature, string query)
    {
        var left = Build(feature);
        var right = Build(query);
        var token = CancellationToken.None;

        if (Dimension(left) == 0)
        {
            foreach (var position in new[] { 3, 4, 5 })
            {
                Assert.False(_relations.Relate(left, right, At(position, "T"), token));
            }
        }

        if (Dimension(right) == 0)
        {
            foreach (var position in new[] { 1, 4, 7 })
            {
                Assert.False(_relations.Relate(left, right, At(position, "T"), token));
            }
        }
    }

    /// <summary>
    /// The second report, pinned as it measures out: two squares meeting at a
    /// corner are one cell apart from two squares sharing an edge, and the
    /// cell is position 5 — boundary against boundary, dimension zero against
    /// dimension one. A vertex is not an edge, and the engine says so in the
    /// one cell that distinguishes them.
    /// </summary>
    [Fact]
    public void Two_areas_touching_at_a_vertex_read_a_point_where_they_share_an_edge_they_read_a_curve()
    {
        var corner = MatrixFor(Square(0, 0, 10, 10), Square(10, 10, 20, 20));
        var edge = MatrixFor(Square(0, 0, 10, 10), Square(10, 0, 20, 10));

        Assert.Equal("FF2F01212", corner);
        Assert.Equal("FF2F11212", edge);
        Assert.Equal('0', corner[4]);
        Assert.Equal('1', edge[4]);

        // Both touch, so the position-5 cell is not what the served Touches
        // masks read: interiors disjoint is positions 1 and 4, and both pairs
        // have those empty.
        foreach (var (name, matrix) in new[] { ("corner", corner), ("edge", edge) })
        {
            Assert.Equal('F', matrix[0]);
            Assert.Equal('F', matrix[3]);
        }
    }

    /// <summary>
    /// The cross-check the bead said the served surface could not make: every
    /// pattern the query path serves, asked against the provider's own named
    /// predicate for the same pair and operand order. The two agree on every
    /// verb and every pair except the sixteen
    /// <see cref="DocumentedDivergences"/> names — and a divergence outside
    /// that table fails here, which is the point: a pattern that leans on a
    /// cell the engine reports loosely should fail a test, not a served query.
    /// </summary>
    [Theory]
    [MemberData(nameof(PairsByRelation))]
    public void The_served_table_agrees_with_the_reference_predicate_except_where_documented(string feature, string query, string relation)
    {
        var left = Build(feature);
        var right = Build(query);
        var served = Served(relation, left, right);
        var reference = Reference(relation, GeometryAdapter.ToNts(left), GeometryAdapter.ToNts(right));

        if (served == reference)
        {
            return;
        }

        var key = $"{relation}|{feature}|{query}";
        Assert.True(DocumentedDivergences.ContainsKey(key), $"'{key}' diverges from the reference predicate and is not documented: matrix {MatrixFor(left, right)}.");
    }

    /// <summary>
    /// The other half of the same pin: the documented table is the whole of
    /// it. A divergence that stops happening is as much a change in what the
    /// engine reports as one that starts, and a client reading this file has
    /// to be able to rely on the list being closed.
    /// </summary>
    [Fact]
    public void The_documented_divergences_are_exactly_the_measured_ones()
    {
        var measured = new HashSet<string>();

        foreach (var (feature, _) in Fixtures)
        {
            foreach (var (query, _) in Fixtures)
            {
                foreach (var (relation, _) in Relations)
                {
                    var left = Build(feature);
                    var right = Build(query);
                    if (Served(relation, left, right) != Reference(relation, GeometryAdapter.ToNts(left), GeometryAdapter.ToNts(right)))
                    {
                        measured.Add($"{relation}|{feature}|{query}");
                    }
                }
            }
        }

        Assert.Equal(DocumentedDivergences.Keys.OrderBy(key => key, StringComparer.Ordinal), measured.OrderBy(key => key, StringComparer.Ordinal));
    }

    /// <summary>
    /// Every documented divergence in the first class is the same shape, and
    /// this is the shape: the line's interior reaches the area's boundary and
    /// the area's exterior never reaches the line's interior. A pair that
    /// diverges for some other reason is a new finding, not this one.
    /// </summary>
    [Theory]
    [MemberData(nameof(PairsByRelation))]
    public void A_documented_divergence_is_a_line_reaching_an_area_boundary_it_never_leaves(string feature, string query, string relation)
    {
        var key = $"{relation}|{feature}|{query}";
        if (!DocumentedDivergences.TryGetValue(key, out var why) || !why.StartsWith("the line's interior", StringComparison.Ordinal))
        {
            return;
        }

        var matrix = MatrixFor(Build(feature), Build(query));

        // Position 4 non-empty, position 7 empty: the contact is there and
        // the line never leaves.
        Assert.NotEqual('F', matrix[3]);
        Assert.Equal('F', matrix[6]);
    }

    /// <summary>
    /// The failure and the cancellation paths of the verb, over the same
    /// fixtures: a malformed pattern is rejected by the grammar before the
    /// provider is asked, and a cancelled token is observed rather than
    /// answered. Both are the verb's own behaviour and neither depends on the
    /// cell semantics above, so they are pinned once here rather than in every
    /// characterisation.
    /// </summary>
    [Fact]
    public async Task The_verb_still_rejects_a_malformed_pattern_and_observes_cancellation()
    {
        var line = Line((5, 2), (5, 8));
        var point = Point(5, 5);

        var rejected = Assert.Throws<Spatial.Contracts.SpatialException>(
            () => _relations.Relate(line, point, "T*T***T**X", CancellationToken.None));
        Assert.Equal("invalid.arguments", rejected.Code);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        Assert.Throws<OperationCanceledException>(
            () => _relations.Relate(line, point, "T********", cancelled.Token));
    }

    /// <summary>The served answer for one verb and one operand order.</summary>
    private bool Served(string relation, IGeometry left, IGeometry right)
    {
        var patterns = Selected(relation, Dimension(left), Dimension(right));
        return patterns.Any(pattern => _relations.Relate(left, right, pattern, CancellationToken.None));
    }

    /// <summary>
    /// The masks a verb is read with for a dimension pair, which is the served
    /// table's own rule: a relation the standard states for one dimension pair
    /// only reads false at every other pair rather than asking a mask that
    /// pair was never given.
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
        _ => Relations.First(masks => masks.Name == relation).Masks,
    };

    /// <summary>
    /// The provider's own named predicates, asked the pair directly rather
    /// than through a pattern.
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

    /// <summary>The topological dimension: points 0, lines 1, areas 2.</summary>
    private static int Dimension(IGeometry geometry) => geometry.Type switch
    {
        GeometryType.Point or GeometryType.MultiPoint => 0,
        GeometryType.LineString or GeometryType.MultiLineString => 1,
        _ => 2,
    };

    /// <summary>
    /// A pair's exact matrix, reconstructed from nine single-cell questions —
    /// "is this cell non-empty?", and when it is "is it a point, a curve or an
    /// area?" — so each cell is answered on its own and the string is the
    /// matrix rather than a reading of one.
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

        return CellDimensions.Single(dimension => _relations.Relate(
            left, right, At(position, dimension.ToString()), CancellationToken.None));
    }

    /// <summary>The three cell dimensions, asked in that order.</summary>
    private static readonly char[] CellDimensions = ['0', '1', '2'];

    /// <summary>The all-wildcard pattern with one cell replaced by <paramref name="symbol"/>.</summary>
    private static string At(int position, string symbol) =>
        "*********"[..position] + symbol + "*********"[(position + 1)..];

    private static IGeometry Build(string name) =>
        Fixtures.First(fixture => fixture.Name == name).Build();

    private static Polygon Square(double minX, double minY, double maxX, double maxY) => GeometryFactory.CreatePolygon(
    [
        new Coordinate(minX, minY),
        new Coordinate(maxX, minY),
        new Coordinate(maxX, maxY),
        new Coordinate(minX, maxY),
        new Coordinate(minX, minY),
    ]);

    private static LineString Line(params (double X, double Y)[] coordinates) => GeometryFactory.CreateLineString(
    [
        .. coordinates.Select(coordinate => new Coordinate(coordinate.X, coordinate.Y)),
    ]);

    private static Point Point(double x, double y) => GeometryFactory.CreatePoint(x, y);
}