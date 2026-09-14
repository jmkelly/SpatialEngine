using Spatial.Adapter.GeoServices;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The MapServer legend family units (ADR-0055): swatch bytes carry the PNG
/// signature, style-less layers fall back to a neutral swatch, and the
/// classifier rejects bad input with typed errors and honours cancellation.
/// </summary>
public sealed class MapLegendTests
{
    private static DatasetDescription Cities(params (string Name, AttributeValue Value)[] attributes)
    {
        var fields = new List<FieldDefinition> { new("geometry", AttributeKind.Geometry) };
        fields.AddRange(attributes.Select(attribute => new FieldDefinition(attribute.Name, attribute.Value.Kind)));
        var schema = new FeatureSchema(fields);
        return new DatasetDescription("demo.cities", "demo", "cities", "geometry", 4326, "Point", 0, [], schema);
    }

    private static Feature Row(FeatureSchema schema, params AttributeValue[] values)
    {
        var attributes = new AttributeValue[schema.Fields.Count];
        attributes[0] = AttributeValue.FromGeometry(GeometryFactory.CreatePoint(new Coordinate(0, 0), null));
        for (var i = 0; i < values.Length; i++)
        {
            attributes[i + 1] = values[i];
        }

        return new Feature(new FeatureId("row"), schema, attributes);
    }

    private sealed class MemoryStore(IReadOnlyList<Feature> features) : IFeatureStore
    {
        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(features[0].Schema, features)];
            return Task.FromResult(batches);
        }

        public Task<IReadOnlyList<FeatureBatch>> QueryAsync(string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default) =>
            ScanAsync(dataset, cancellationToken);

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    [Fact]
    public void Swatch_urls_are_deterministic_across_instances()
    {
        static string Url()
        {
            var info = new MapLayerInfo(
                new PublishedLayer(0, "demo.cities", "Cities", """[{"type":"circle","paint":{"circle-color":"#ff0000","circle-radius":6}}]"""),
                Cities(("population", AttributeValue.FromInt64(1))),
                Envelope.Empty);
            return MapLegend.Legend([info]).Layers.Single().Legend.Single().Url;
        }

        var first = Url();
        var second = Url();

        Assert.Equal(first, second);
        Assert.Equal(32, first.Length);
        Assert.Matches("^[0-9a-f]{32}$", first);
    }

    [Fact]
    public void A_simple_layer_legends_a_single_png_swatch()
    {
        var info = new MapLayerInfo(
            new PublishedLayer(0, "demo.cities", "Cities", """[{"type":"circle","paint":{"circle-color":"#ff0000","circle-radius":6}}]"""),
            Cities(("population", AttributeValue.FromInt64(1))),
            Envelope.Empty);

        var layer = MapLegend.Legend([info]).Layers.Single();

        Assert.Equal(0, layer.LayerId);
        var entry = Assert.Single(layer.Legend);
        Assert.Equal("image/png", entry.ContentType);
        var bytes = Convert.FromBase64String(entry.ImageData);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], bytes[..8]);
    }

    [Fact]
    public void A_style_less_layer_legends_a_neutral_swatch()
    {
        var info = new MapLayerInfo(
            new PublishedLayer(0, "demo.cities", "Cities"),
            Cities(("population", AttributeValue.FromInt64(1))),
            Envelope.Empty);

        var layer = MapLegend.Legend([info]).Layers.Single();

        var bytes = Convert.FromBase64String(Assert.Single(layer.Legend).ImageData);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], bytes[..4]);
        Assert.Equal("0", layer.LegendGroups.Single().Id);
    }

    [Fact]
    public void Query_domains_is_empty_without_a_rendered_field()
    {
        var info = new MapLayerInfo(
            new PublishedLayer(0, "demo.cities", "Cities"),
            Cities(("population", AttributeValue.FromInt64(1))),
            Envelope.Empty);

        var domains = MapLegend.QueryDomains([info]).Domains.Single();

        Assert.Equal(0, domains.LayerId);
        Assert.Empty(domains.Domains);
    }

    [Fact]
    public async Task Generate_class_breaks_marks_equal_intervals()
    {
        var dataset = Cities(("population", AttributeValue.FromInt64(1)));
        var store = new MemoryStore([
            Row(dataset.Schema, AttributeValue.FromInt64(0)),
            Row(dataset.Schema, AttributeValue.FromInt64(100)),
        ]);

        var renderer = await MapGenerateRenderer.GenerateAsync(
            store, dataset,
            """{"type":"classBreaksDef","classificationField":"population","breakCount":2,"classificationMethod":"esriClassifyEqualInterval"}""",
            null, CancellationToken.None);

        Assert.Equal("classBreaks", renderer.Type);
        Assert.Equal("population", renderer.Field);
        Assert.Equal(0, renderer.MinValue);
        Assert.Equal([50, 100], renderer.ClassBreakInfos!.Select(info => info.ClassMaxValue));
    }

    [Fact]
    public async Task Generate_unique_values_lists_each_value()
    {
        var dataset = Cities(("country", AttributeValue.FromString("x")));
        var store = new MemoryStore([
            Row(dataset.Schema, AttributeValue.FromString("DE")),
            Row(dataset.Schema, AttributeValue.FromString("FR")),
            Row(dataset.Schema, AttributeValue.FromString("DE")),
        ]);

        var renderer = await MapGenerateRenderer.GenerateAsync(
            store, dataset, """{"type":"uniqueValueDef","uniqueValueFields":["country"]}""",
            null, CancellationToken.None);

        Assert.Equal("uniqueValue", renderer.Type);
        Assert.Equal(["DE", "FR"], renderer.UniqueValueInfos!.Select(info => info.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"type":"quantileDef"}""")]
    [InlineData("""{"type":"classBreaksDef"}""")]
    [InlineData("""{"type":"classBreaksDef","classificationField":"missing","breakCount":2}""")]
    [InlineData("""{"type":"classBreaksDef","classificationField":"country","breakCount":2}""")]
    [InlineData("""{"type":"classBreaksDef","classificationField":"population","breakCount":0}""")]
    [InlineData("""{"type":"classBreaksDef","classificationField":"population","breakCount":2,"classificationMethod":"esriClassifyQuantile"}""")]
    [InlineData("""{"type":"uniqueValueDef","uniqueValueFields":[]}""")]
    [InlineData("""{"type":"uniqueValueDef","uniqueValueFields":["population","country"]}""")]
    public async Task Generate_rejects_bad_classifications(string? classificationDef)
    {
        var dataset = Cities(("population", AttributeValue.FromInt64(1)), ("country", AttributeValue.FromString("x")));
        var store = new MemoryStore([Row(dataset.Schema, AttributeValue.FromInt64(1), AttributeValue.FromString("DE"))]);

        var exception = await Assert.ThrowsAsync<EsriInteropException>(() =>
            MapGenerateRenderer.GenerateAsync(store, dataset, classificationDef, null, CancellationToken.None));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
    }

    [Fact]
    public async Task Generate_rejects_a_where_clause_that_matches_nothing()
    {
        var dataset = Cities(("population", AttributeValue.FromInt64(1)));
        var store = new MemoryStore([Row(dataset.Schema, AttributeValue.FromInt64(1))]);

        var exception = await Assert.ThrowsAsync<EsriInteropException>(() =>
            MapGenerateRenderer.GenerateAsync(
                store, dataset,
                """{"type":"classBreaksDef","classificationField":"population","breakCount":2}""",
                "population > 10000000", CancellationToken.None));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
    }

    [Fact]
    public async Task Generate_honours_cancellation()
    {
        var dataset = Cities(("population", AttributeValue.FromInt64(1)));
        var store = new MemoryStore([Row(dataset.Schema, AttributeValue.FromInt64(1))]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MapGenerateRenderer.GenerateAsync(
                store, dataset,
                """{"type":"classBreaksDef","classificationField":"population","breakCount":2}""",
                null, new CancellationToken(canceled: true)));
    }
}
