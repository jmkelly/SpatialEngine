namespace Spatial.Esri.Codec.Tests;

/// <summary>
/// Quality-loop pass 2: cover the pure operator/literal rendering helpers
/// (cheap lever — both are low-complexity switches whose CRAP is all
/// uncovered lines).
/// </summary>
public sealed class OperatorTextTests
{
    [Fact]
    public void OperatorText_renders_every_operator()
    {
        var cases = new (ComparisonOperator Op, string Expected)[]
        {
            (ComparisonOperator.Equals, "="),
            (ComparisonOperator.NotEquals, "<>"),
            (ComparisonOperator.LessThan, "<"),
            (ComparisonOperator.LessOrEqual, "<="),
            (ComparisonOperator.GreaterThan, ">"),
            (ComparisonOperator.GreaterOrEqual, ">="),
            (ComparisonOperator.Like, "LIKE"),
        };
        foreach (var (op, expected) in cases)
        {
            Assert.Equal(expected, EsriFilterLogic.OperatorText(op));
        }
    }

    [Fact]
    public void OperatorText_rejects_unknown_operators() =>
        Assert.ThrowsAny<Exception>(() => EsriFilterLogic.OperatorText((ComparisonOperator)999));

    [Fact]
    public void Literal_renders_scalar_kinds()
    {
        Assert.Equal("'a''b'", new Literal(LiteralKind.String, "a'b", 0, false).Render());
        Assert.Equal("7", new Literal(LiteralKind.Integer, null, 7, false).Render());
        Assert.Equal("2.5", new Literal(LiteralKind.Decimal, null, 2.5, false).Render());
        Assert.Equal("TRUE", new Literal(LiteralKind.Boolean, null, 0, true).Render());
        Assert.Equal("NULL", new Literal(LiteralKind.Null, null, 0, false).Render());
    }

    [Fact]
    public void Literal_renders_false_and_datetime()
    {
        Assert.Equal("FALSE", new Literal(LiteralKind.Boolean, null, 0, false).Render());
        Assert.Equal(
            "TIMESTAMP '1970-01-01T00:00:00.001Z'",
            new Literal(LiteralKind.DateTime, null, 1, false).Render());
    }
}
