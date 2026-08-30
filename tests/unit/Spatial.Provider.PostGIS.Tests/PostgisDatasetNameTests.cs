using Spatial.Provider.PostGIS.Core;

namespace Spatial.Provider.PostGIS.Tests;

/// <summary>
/// The strict <c>schema.table</c> dataset-identifier grammar — the injection
/// barrier for generated SQL (ADR-0028).
/// </summary>
public sealed class PostgisDatasetNameTests
{
    [Theory]
    [InlineData("public.places", "public", "places")]
    [InlineData("places", "public", "places")]
    [InlineData("spatial_data.roads_2024", "spatial_data", "roads_2024")]
    [InlineData("_private.t1", "_private", "t1")]
    [InlineData("a.b", "a", "b")]
    public void Valid_names_parse(string text, string schema, string table)
    {
        Assert.True(PostgisDatasetName.TryParse(text, out var name, out _));

        Assert.Equal(schema, name.Schema);
        Assert.Equal(table, name.Table);
        Assert.Equal($"{schema}.{table}", name.Qualified);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(".places")]
    [InlineData("public.")]
    [InlineData("a.b.c")]
    [InlineData("Public.Places")]
    [InlineData("public.plaçes")]
    [InlineData("public.place; drop table x")]
    [InlineData("public.place\" --")]
    [InlineData("1table")]
    [InlineData("public.1table")]
    public void Malformed_or_injection_shaped_names_are_rejected(string? text)
    {
        var ok = PostgisDatasetName.TryParse(text, out _, out var reason);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData("places")]
    [InlineData("roads_2024")]
    [InlineData("_x")]
    public void Column_name_validation_accepts_lowercase_identifiers(string name)
    {
        Assert.True(PostgisDatasetName.IsValidIdentifier(name));
    }

    [Theory]
    [InlineData("Places")]
    [InlineData("place name")]
    [InlineData("place\"x")]
    [InlineData("")]
    [InlineData(null)]
    public void Column_name_validation_rejects_anything_else(string? name)
    {
        Assert.False(PostgisDatasetName.IsValidIdentifier(name));
    }

    [Fact]
    public void Quoted_identifier_uses_quotes()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var name, out _));

        Assert.Equal("\"public\".\"places\"", name.QuoteQualified());
    }
}
