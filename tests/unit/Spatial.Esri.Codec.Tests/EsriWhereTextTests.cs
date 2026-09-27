using Spatial.Core.Features.Query;

namespace Spatial.Esri.Codec.Tests;

/// <summary>
/// The rendering half of the Esri where grammar: the operator and literal
/// text the consuming ArcGIS REST provider puts on the wire, and the patterns
/// <c>LIKE</c> compiles to.
/// </summary>
public sealed class EsriWhereTextTests
{
    [Fact]
    public void OperatorText_renders_every_operator()
    {
        var cases = new (ComparisonOperator Operator, string Expected)[]
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
            Assert.Equal(expected, EsriWhereText.OperatorText(op));
        }
    }

    [Fact]
    public void OperatorText_rejects_unknown_operators() =>
        Assert.ThrowsAny<Exception>(() => EsriWhereText.OperatorText((ComparisonOperator)999));

    [Fact]
    public void Literal_renders_scalar_kinds()
    {
        Assert.Equal("'a''b'", EsriWhereText.Render(Literal.FromText("a'b")));
        Assert.Equal("7", EsriWhereText.Render(Literal.FromInteger("7")));
        Assert.Equal("2.5", EsriWhereText.Render(Literal.FromNumber(2.5)));
        Assert.Equal("TRUE", EsriWhereText.Render(Literal.FromBoolean(true)));
        Assert.Equal("FALSE", EsriWhereText.Render(Literal.FromBoolean(false)));
        Assert.Equal("NULL", EsriWhereText.Render(Literal.Null));
    }

    [Fact]
    public void Literal_renders_a_date_time()
    {
        Assert.Equal(
            "TIMESTAMP '1970-01-01T00:00:00.001Z'",
            EsriWhereText.Render(Literal.FromMilliseconds(1)));
    }

    [Theory]
    [InlineData("Berlin", "Ber%", true)]
    [InlineData("Berlin", "ber%", false)]
    [InlineData("Berlin", "_erlin", true)]
    [InlineData("Berlin", "B", false)]
    [InlineData("Berlin", "%", true)]
    [InlineData("a.c", "a.c", true)]
    [InlineData("abc", "a.c", false)]
    public void Like_patterns_match_whole_values(string value, string pattern, bool expected) =>
        Assert.Equal(expected, EsriLikePattern.IsMatch(value, pattern));
}
