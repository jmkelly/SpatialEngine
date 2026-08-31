using Spatial.PluginSdk.Transformations;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// The <c>authority:code</c> identity parsing shared by
/// <c>spatial.crs.describe@1</c> and <c>spatial.coordinate.transform@1</c>
/// (ADR-0027): well-formed identities parse and round-trip, and every
/// malformed shape is rejected instead of failing the wire contract later.
/// </summary>
public sealed class CrsIdentityTests
{
    [Fact]
    public void Well_formed_identities_parse_and_render()
    {
        var identity = CrsIdentity.Parse("EPSG:4326");

        Assert.Equal("EPSG", identity.Authority);
        Assert.Equal("4326", identity.Code);
        Assert.Equal("EPSG:4326", identity.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("4326")]                  // no separator
    [InlineData(":4326")]                 // empty authority
    [InlineData("EPSG:")]                 // empty code
    [InlineData("EPSG:43 26")]            // whitespace inside code
    [InlineData("EPS G:4326")]            // whitespace inside authority
    [InlineData("EPSG:43-26!")]           // illegal character
    [InlineData("A:B:C")]                 // extra separator is part of the code -> illegal character
    public void Malformed_identities_are_rejected(string? text)
    {
        Assert.False(CrsIdentity.TryParse(text, out var identity));
        Assert.Equal(default, identity);
    }

    [Fact]
    public void Parse_throws_a_format_exception_naming_the_value()
    {
        var exception = Assert.Throws<FormatException>(() => CrsIdentity.Parse("EPSG:"));
        Assert.Contains("'EPSG:'", exception.Message);
    }

    [Theory]
    [InlineData("EPSG:2224")]
    [InlineData("OGC:CRS84")]
    [InlineData("urn.ogc.def:4258")]
    [InlineData("EPSG:EPSG")]
    public void Token_characters_and_length_limits_are_accepted(string text)
    {
        Assert.True(CrsIdentity.TryParse(text, out var identity));
        Assert.Equal(text, identity.ToString());
    }

    [Fact]
    public void Overlong_tokens_are_rejected()
    {
        var tooLong = new string('A', 65);
        Assert.False(CrsIdentity.TryParse($"{tooLong}:1", out _));
        Assert.False(CrsIdentity.TryParse($"EPSG:{tooLong}", out _));
    }
}
