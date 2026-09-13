using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.Rendering.Skia.Pipeline;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Tests;

public sealed class SymbolSceneBuilderTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("label", AttributeKind.String, nullable: true),
        new FieldDefinition("count", AttributeKind.Int64, nullable: true),
        new FieldDefinition("ratio", AttributeKind.Double, nullable: true),
        new FieldDefinition("active", AttributeKind.Boolean, nullable: true),
        new FieldDefinition("token", AttributeKind.Guid, nullable: true),
        new FieldDefinition("at", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    private static readonly RasterViewport Viewport = new(new Envelope(0, 0, 10, 10), 100, 100, "EPSG:4326");

    [Theory]
    [InlineData(AttributeKind.String, "hello", "hello")]
    [InlineData(AttributeKind.Int64, 42L, "42")]
    [InlineData(AttributeKind.Double, 1.5, "1.5")]
    [InlineData(AttributeKind.Boolean, true, "true")]
    [InlineData(AttributeKind.Boolean, false, "false")]
    public void ReadAttribute_FormatsScalarKinds(AttributeKind kind, object value, string expected)
    {
        var (name, attribute) = Attribute(kind, value);
        var feature = FeatureWith((name, attribute));

        Assert.Equal(expected, SymbolSceneBuilder.ReadAttribute(feature, name));
    }

    [Fact]
    public void ReadAttribute_FormatsGuidAndDateTimeOffset()
    {
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var at = new DateTimeOffset(2026, 9, 16, 12, 30, 0, TimeSpan.Zero);
        var feature = FeatureWith(("token", AttributeValue.FromGuid(guid)), ("at", AttributeValue.FromDateTimeOffset(at)));

        Assert.Equal(guid.ToString(), SymbolSceneBuilder.ReadAttribute(feature, "token"));
        Assert.Equal(
            at.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            SymbolSceneBuilder.ReadAttribute(feature, "at"));
    }

    [Fact]
    public void ReadAttribute_ReturnsNullForGeometryAndMissingNames()
    {
        var feature = FeatureWith(("geometry", AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 1))));

        Assert.Null(SymbolSceneBuilder.ReadAttribute(feature, "geometry"));
        Assert.Null(SymbolSceneBuilder.ReadAttribute(feature, "absent"));
    }

    [Fact]
    public void Build_ResolvesTemplatesAndOrdersCandidatesByIdentity()
    {
        var builder = new SymbolSceneBuilder(new IdentityTransforms(), new FakeOperations());
        var layer = Layer("{label}");
        var features = Features(SymbolFeature("Beta", 5, 5), SymbolFeature("Alpha", 4, 4));

        var scene = builder.Build(layer, features, Viewport, Symbol(layer), CancellationToken.None);

        Assert.Equal(["Alpha", "Beta"], scene.Symbols.Select(symbol => symbol.Text));
    }

    [Fact]
    public void Build_SkipsMissingTokensAndCullsOutsideTheViewport()
    {
        var builder = new SymbolSceneBuilder(new IdentityTransforms(), new FakeOperations());
        var layer = Layer("{label}");
        var features = Features(
            SymbolFeature("Inside", 5, 5),
            SymbolFeature("Outside", 50, 50),
            new Feature(
                new FeatureId("Missing"),
                Schema,
                Base(AttributeValue.Null, GeometryFactory.CreatePoint(5, 5))));

        var scene = builder.Build(layer, features, Viewport, Symbol(layer), CancellationToken.None);

        var symbol = Assert.Single(scene.Symbols);
        Assert.Equal("Inside", symbol.Text);
    }

    [Fact]
    public void Build_AppliesTheLayerFilter()
    {
        var builder = new SymbolSceneBuilder(new IdentityTransforms(), new FakeOperations());
        var layer = Layer("{label}") with { Filter = new EqualsFilter("label", AttributeValue.FromString("Beta")) };
        var features = Features(SymbolFeature("Alpha", 5, 5), SymbolFeature("Beta", 4, 4));

        var scene = builder.Build(layer, features, Viewport, Symbol(layer), CancellationToken.None);

        Assert.Equal(["Beta"], scene.Symbols.Select(symbol => symbol.Text));
    }

    private static DrawLayer Layer(string template) =>
        new("labels", "demo", DrawKind.Symbol, 0, 24, true, StyleFilter.Always, SymbolPaintFor(template));

    private static SymbolPaint Symbol(DrawLayer layer) => (SymbolPaint)layer.Paint;

    private static SymbolPaint SymbolPaintFor(string template) =>
        new(new SymbolOptions(
            template, [], 16, new StyleColor(0, 0, 0), StyleColor.Transparent, 0, SymbolAnchor.Center, 0, 0, 2, false, null, 1, false));

    private static LayerFeatures Features(params IFeature[] features) => new(4326, "geometry", features);

    private static Feature SymbolFeature(string label, double x, double y) =>
        new(new FeatureId(label), Schema, Base(AttributeValue.FromString(label), GeometryFactory.CreatePoint(x, y)));

    private static AttributeValue[] Base(AttributeValue label, IGeometry geometry)
    {
        var attributes = new AttributeValue[Schema.Count];
        attributes[0] = label;
        attributes[^1] = AttributeValue.FromGeometry(geometry);
        return attributes;
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

    private static (string Name, AttributeValue Value) Attribute(AttributeKind kind, object value) => kind switch
    {
        AttributeKind.String => ("label", AttributeValue.FromString((string)value)),
        AttributeKind.Int64 => ("count", AttributeValue.FromInt64((long)value)),
        AttributeKind.Double => ("ratio", AttributeValue.FromDouble((double)value)),
        _ => ("active", AttributeValue.FromBoolean((bool)value)),
    };
}
