using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Rendering.Skia.Pipeline;

namespace Spatial.Rendering.Skia.Tests;

public sealed class SymbolTemplateResolverTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("label", AttributeKind.String, nullable: true),
        new FieldDefinition("count", AttributeKind.Int64, nullable: true),
    ]);

    [Fact]
    public void An_empty_template_resolves_to_null()
    {
        Assert.Null(SymbolTemplateResolver.Resolve(string.Empty, FeatureWith(("label", AttributeValue.FromString("a")))));
    }

    [Fact]
    public void A_template_without_tokens_is_returned_verbatim()
    {
        Assert.Equal("hello", SymbolTemplateResolver.Resolve("hello", FeatureWith()));
    }

    [Fact]
    public void Tokens_are_replaced_by_attribute_text()
    {
        var feature = FeatureWith(("label", AttributeValue.FromString("Main")), ("count", AttributeValue.FromInt64(7)));

        Assert.Equal("Main-7!", SymbolTemplateResolver.Resolve("{label}-{count}!", feature));
    }

    [Fact]
    public void A_token_for_a_missing_attribute_resolves_to_null()
    {
        var feature = FeatureWith(("label", AttributeValue.FromString("Main")));

        Assert.Null(SymbolTemplateResolver.Resolve("{label}-{absent}", feature));
    }

    [Fact]
    public void An_unterminated_token_is_kept_as_literal_text()
    {
        var feature = FeatureWith(("label", AttributeValue.FromString("Main")));

        Assert.Equal("a{label", SymbolTemplateResolver.Resolve("a{label", feature));
    }

    private static Feature FeatureWith(params (string Name, AttributeValue Value)[] values)
    {
        var attributes = new AttributeValue[Schema.Count];
        foreach (var (name, value) in values)
        {
            attributes[Schema.IndexOf(name)] = value;
        }

        return new Feature(new FeatureId("feature"), Schema, attributes);
    }
}
