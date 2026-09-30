using Spatial.Contracts;
using Spatial.Core.Geometry;

namespace Spatial.Operations.NetTopologySuite.Tests;

/// <summary>
/// The DE-9IM relation verb (ADR-0036) read as the OGC defines it: a
/// nine-cell intersection pattern, answered exactly (SpatialEngine-imj).
///
/// The bead that produced this file reported that NetTopologySuite 2.6
/// "does not answer a wildcard pattern the same way it answers the exact
/// intersection matrix", and that a DE-9IM cell which is empty-or-point
/// stops matching <c>T</c> and <c>0</c> once other cells are wildcarded —
/// with the unit square against the line (5,5)-(15,15), whose exact matrix
/// is <c>1020F1102</c>:
///
/// <code>
/// Relate(square, line, "1020F1102") = true   (the exact matrix)
/// Relate(square, line, "**T**")   = false
/// Relate(square, line, "**0**")   = false
/// Relate(square, line, "1*2F0*1*2") = false
/// </code>
///
/// The premise does not survive contact with the grammar, and the two
/// findings are the reason the file is here:
///
/// <list type="number">
/// <item><description><c>"**T**"</c> and <c>"**0**"</c> are not DE-9IM at
/// all. A pattern is nine cells, one per row-column pair of the matrix, so a
/// five-character pattern is not a shorter spelling of one — it is not a
/// pattern. The engine rejects it, and this file pins the rejection as
/// <c>invalid.arguments</c> carrying the grammar, rather than leaving the
/// caller to read a provider's wording about a length.</description></item>
/// <item><description><c>"1*2F0*1*2"</c> is not a wildcarding of
/// <c>"1020F1102"</c>. It is the same pattern with three cells widened — and
/// it names position 4 <c>F</c> where the matrix reads <c>0</c>, so it is
/// false, correctly. The position numbering runs
/// interior∩interior, interior∩boundary, interior∩exterior,
/// boundary∩interior, boundary∩boundary, boundary∩exterior and the three
/// transposed rows, so position 4 is <em>the square's boundary against the
/// line's interior</em>, which the crossing line does reach. Reading
/// position 5 as the contact is the off-by-one that made the report look
/// like a matcher bug.</description></item>
/// </list>
///
/// What is left is the guarantee worth pinning, and it holds: a pattern is
/// answered exactly as the matrix reads, cell by cell, for every symbol in
/// every position — including the empty-or-point cells the report named
/// (<see cref="A_pattern_is_answered_exactly_as_its_matrix_reads"/> over a
/// hand-computed matrix, no provider type in the assertion). The two
/// remaining tests record the DE-9IM vocabulary gap the bead found
/// alongside the phantom divergence: a point sitting on a line is
/// nameless, because a point's own boundary is empty, so the meeting lands
/// in neither boundary cell and the matrix names no relation for it.
/// </summary>
public sealed class NtsGeometryRelationsTests
{
    private readonly NtsGeometryRelations _relations = new();

    /// <summary>
    /// The exact matrix of the unit square against the line (5,5)-(15,15),
    /// hand-computed rather than read back from the provider, so the pins
    /// below cannot agree with a provider bug by construction: interiors meet
    /// in dimension one (the segment inside the square), the square's
    /// interior meets the line's two endpoints in dimension zero, the
    /// square's boundary misses the line's interior entirely (the crossing
    /// points are interior points of the line, not endpoints), and so on.
    /// </summary>
    private const string SquareAgainstCrossingLine = "1020F1102";

    /// <summary>The same line, at a point sitting on its near endpoint — the point the bead named.</summary>
    private const string LineAgainstEndpointPoint = "FF10F0FF2";

    /// <summary>The same line, at a point sitting in its interior.</summary>
    private const string LineAgainstInteriorPoint = "0F1FF0FF2";

    /// <summary>
    /// A 20×20 square with a 10×10 hole, against a 4×4 square inside that
    /// hole. The pair shares no point at all, yet its matrix has six
    /// non-empty cells: everything the two reach is exterior, which is why
    /// it is the row that catches a touches pattern reading a contact where
    /// there is none.
    /// </summary>
    private const string DonutAgainstSquareInItsHole = "FF2FF1212";

    /// <summary>The three cell dimensions, asked in that order.</summary>
    private static readonly char[] Dimensions = ['0', '1', '2'];

    /// <summary>The symbols a pattern cell may hold, each one asked in all nine positions.</summary>
    private static readonly string[] Symbols = ["T", "F", "0", "1", "2"];

    [Fact]
    public void The_pinned_matrix_reads_as_the_report_states_it()
    {
        var (square, line) = SquareAndCrossingLine();

        Assert.True(_relations.Relate(square, line, SquareAgainstCrossingLine, CancellationToken.None));
        Assert.False(_relations.Relate(square, line, "1*2F0*1*2", CancellationToken.None));
    }

    /// <summary>
    /// The report's two short patterns, pinned as the grammar rejects them:
    /// a DE-9IM pattern is nine cells drawn from <c>T</c> (non-empty),
    /// <c>F</c> (empty), <c>0</c>/<c>1</c>/<c>2</c> (that dimension) and
    /// <c>*</c> (don't care), and a five-character string is not one. The
    /// failure is <c>invalid.arguments</c> and it names the grammar, so a
    /// caller learns what to write rather than what a provider's parser
    /// objected to.
    /// </summary>
    [Theory]
    [InlineData("**T**")]
    [InlineData("**0**")]
    [InlineData("T*T***T**T")]
    [InlineData("T*T***T* ")]
    [InlineData("T*T***TX*")]
    [InlineData("T*T***T:E")]
    public void A_pattern_that_is_not_nine_de9im_cells_is_rejected_with_the_grammar(string pattern)
    {
        var (square, line) = SquareAndCrossingLine();

        var error = Assert.Throws<SpatialException>(
            () => _relations.Relate(square, line, pattern, CancellationToken.None));

        Assert.Equal(SpatialException.InvalidArguments, error.Code);
        Assert.Contains("nine", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("T", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guarantee the report was looking for and did not find: a pattern
    /// is answered exactly as the matrix reads, cell by cell. Every symbol
    /// is asked in every position of a hand-computed matrix — so the
    /// empty-or-point cells, the cells the report claimed stopped matching
    /// <c>T</c> and <c>0</c>, are asked in all nine positions and answered
    /// from the matrix rather than from the provider.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Every_symbol_in_every_position_is_answered_as_the_matrix_reads(int position)
    {
        var (square, line) = SquareAndCrossingLine();
        var token = CancellationToken.None;

        foreach (var symbol in Symbols)
        {
            var pattern = Wildcard(SquareAgainstCrossingLine, position, symbol);

            Assert.Equal(
                MatrixReads(SquareAgainstCrossingLine, pattern),
                _relations.Relate(square, line, pattern, token));
        }
    }

    /// <summary>
    /// The whole pattern table the engine serves, read against hand-computed
    /// matrices rather than against the provider — the report's third
    /// acceptance criterion, at the verb every consumer goes through. Each
    /// row is a matrix and the patterns whose answer over it is not in
    /// doubt, so the table is pinned to DE-9IM and not to whatever the
    /// provider happens to do with a wildcard.
    /// </summary>
    [Theory]
    // The crossing line against the square: a contact in position 4, an
    // empty boundary∩boundary in position 5, and a 2-cell exterior.
    [InlineData(SquareAgainstCrossingLine, "T*T***T**", true)]
    [InlineData(SquareAgainstCrossingLine, "T**T*****", true)]
    [InlineData(SquareAgainstCrossingLine, "T*****FF*", false)]
    [InlineData(SquareAgainstCrossingLine, "***T*****", true)]
    [InlineData(SquareAgainstCrossingLine, "F***T****", false)]
    // The square against itself: equal interiors, so contains and within,
    // and neither overlap nor touch.
    [InlineData(DonutAgainstSquareInItsHole, "T*T***T**", false)]
    [InlineData(DonutAgainstSquareInItsHole, "T*****FF*", false)]
    // A square in a hole, against the polygon that has the hole: the
    // interiors are disjoint and the only non-empty cells are exteriors, so
    // no relation but disjointness is nameable.
    [InlineData(DonutAgainstSquareInItsHole, "F**T*****", false)]
    [InlineData(DonutAgainstSquareInItsHole, "F***T****", false)]
    [InlineData(DonutAgainstSquareInItsHole, "********F", false)]
    // A line against a point at its endpoint (see the vocabulary gap below)
    // and against a point in its interior: the interior point is named by
    // contains and within, the endpoint point by neither.
    [InlineData(LineAgainstEndpointPoint, "T*****FF*", false)]
    [InlineData(LineAgainstEndpointPoint, "T*F**F***", false)]
    [InlineData(LineAgainstInteriorPoint, "T*****FF*", true)]
    [InlineData(LineAgainstInteriorPoint, "T*F**F***", false)]
    public void A_pattern_is_answered_exactly_as_its_matrix_reads(string matrix, string pattern, bool served)
    {
        var (left, right) = matrix switch
        {
            SquareAgainstCrossingLine => SquareAndCrossingLine(),
            DonutAgainstSquareInItsHole => (Donut(), Square(8, 8, 12, 12)),
            LineAgainstEndpointPoint => (Line(), GeometryFactory.CreatePoint(5, 5)),
            LineAgainstInteriorPoint => (Line(), GeometryFactory.CreatePoint(10, 10)),
            _ => throw new ArgumentOutOfRangeException(nameof(matrix), matrix, "no fixture has this matrix"),
        };

        Assert.Equal(served, MatrixReads(matrix, pattern));
        Assert.Equal(served, _relations.Relate(left, right, pattern, CancellationToken.None));
    }

    /// <summary>
    /// The DE-9IM vocabulary gap the bead found alongside the phantom
    /// divergence, pinned: a point sitting on a line relates to nothing the
    /// matrix can name. A point's own boundary is empty, so the meeting
    /// cannot show up in a boundary∩boundary cell, and the only cell it does
    /// reach is the line's boundary against the point's interior — which
    /// names a contact and nothing else. <c>Contains</c> and <c>Within</c>
    /// both read false; the OGC intersect union reads true, because the point
    /// is in the line's boundary, which is where the union's fourth member
    /// looks. NetTopologySuite's own <c>Touches</c> agrees with the contact
    /// reading, so this is DE-9IM's vocabulary and not a defect to work
    /// around: a <c>covers</c>-style verb would be a different question
    /// ("is every point of B in the closed set A?"), it is not one of the
    /// served protocol's relation names, and it is not expressible as a
    /// single nine-cell pattern — it is the negation of the disjoint
    /// pattern. ADR-0036 records the decision to document it rather than
    /// invent a verb.
    /// </summary>
    [Fact]
    public void A_point_at_a_line_endpoint_is_nameable_only_as_a_contact()
    {
        var line = Line();
        var endpoint = GeometryFactory.CreatePoint(5, 5);
        var interior = GeometryFactory.CreatePoint(10, 10);
        var token = CancellationToken.None;

        Assert.Equal(LineAgainstEndpointPoint, MatrixFor(line, endpoint));
        Assert.Equal(LineAgainstInteriorPoint, MatrixFor(line, interior));

        // Neither contains nor within: the matrix names no relation at all.
        Assert.False(_relations.Relate(line, endpoint, "T*****FF*", token));
        Assert.False(_relations.Relate(line, endpoint, "T*F**F***", token));

        // The contact and the meet are nameable; a point in the line's
        // interior is a different case entirely, and is named as contained.
        Assert.True(_relations.Relate(line, endpoint, "F**T*****", token));
        Assert.True(_relations.Relate(line, endpoint, "***T*****", token));
        Assert.True(_relations.Relate(line, interior, "T*****FF*", token));
    }

    /// <summary>
    /// Reconstructs a pair's exact matrix out of nine independent
    /// single-cell questions — each cell asked as "is this cell non-empty?"
    /// and, when it is, "is it a point, a curve or an area?" — so the
    /// hand-computed matrix strings this file pins are the matrices the
    /// verb actually serves, read a different way round from the patterns
    /// the rest of the file asks with.
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
        if (!_relations.Relate(left, right, Wildcard("*********", position, "T"), CancellationToken.None))
        {
            Assert.True(
                _relations.Relate(left, right, Wildcard("*********", position, "F"), CancellationToken.None),
                $"cell {position} is neither empty nor non-empty");
            return 'F';
        }

        return Dimensions.Single(dimension => _relations.Relate(
            left, right, Wildcard("*********", position, dimension.ToString()), CancellationToken.None));
    }

    /// <summary>
    /// Whether a pattern holds over an exact matrix, read from the matrix
    /// string alone — the reference reading, independent of any provider.
    /// </summary>
    private static bool MatrixReads(string matrix, string pattern) =>
        matrix.Zip(pattern, (cell, asked) => asked switch
        {
            '*' => true,
            'T' => cell != 'F',
            'F' => cell == 'F',
            _ => cell == asked,
        }).All(holds => holds);

    /// <summary>The pattern over <paramref name="matrix"/> with one cell replaced by <paramref name="symbol"/>.</summary>
    private static string Wildcard(string matrix, int position, string symbol) =>
        matrix[..position] + symbol + matrix[(position + 1)..];

    private static (IGeometry Square, IGeometry Line) SquareAndCrossingLine() => (Square(), Line());

    private static Polygon Square() => Square(0, 0, 10, 10);

    private static Polygon Square(double minX, double minY, double maxX, double maxY) => GeometryFactory.CreatePolygon(
    [
        new Coordinate(minX, minY),
        new Coordinate(maxX, minY),
        new Coordinate(maxX, maxY),
        new Coordinate(minX, maxY),
        new Coordinate(minX, minY),
    ]);

    private static Polygon Donut() => GeometryFactory.CreatePolygon(
        Square(0, 0, 20, 20).ExteriorRing,
        [Square(5, 5, 15, 15).ExteriorRing]);

    private static LineString Line() => GeometryFactory.CreateLineString(
    [
        new Coordinate(5, 5),
        new Coordinate(15, 15),
    ]);
}
