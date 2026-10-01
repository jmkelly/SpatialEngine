using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Rendering.Skia.Tests;

/// <summary>
/// T-040: per-request temporal filtering in the render pipeline. A
/// <see cref="MapLayerSource"/> may carry a <see cref="MapTimeExtent"/> (the
/// export <c>time</c> parameter); features with a date attribute outside the
/// extent are dropped, features without date values pass through — the same
/// rule the query path applies (ArcGIS Server ignores <c>time</c> on
/// non-time-aware layers).
/// </summary>
public sealed class TemporalRenderTests
{
    private static readonly FeatureSchema DatedSchema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("observed", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static readonly FeatureSchema DatelessSchema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static readonly DateTimeOffset Inside = new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Outside = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string CircleStyle = """
        { "version": 8, "layers": [
            { "id": "places", "type": "circle", "source-layer": "demo.places",
              "paint": { "circle-color": "#ff0000", "circle-radius": 8 } } ] }
        """;

    private static Feature Dated(string name, double x, DateTimeOffset observed) => new(
        new FeatureId(name),
        DatedSchema,
        [
            AttributeValue.FromString(name),
            AttributeValue.FromDateTimeOffset(observed),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, 0, CoordinateReference.Epsg(4326))),
        ]);

    private static Feature Dateless(string name, double x) => new(
        new FeatureId(name),
        DatelessSchema,
        [
            AttributeValue.FromString(name),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, 0, CoordinateReference.Epsg(4326))),
        ]);

    private static MapTimeExtent Window(DateTimeOffset start, DateTimeOffset end) =>
        new(start.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds());

    private static MapRenderRequest Request(IFeatureStore store, MapTimeExtent? time) => new(
        new RasterViewport(new Envelope(-10, -10, 10, 10), 100, 100, "EPSG:4326"),
        CircleStyle,
        [new MapLayerSource("demo.places", store, new FakeCatalogue(4326), Time: time)]);

    /// <summary>
    /// A designated layer is read by the extent rule in the render pipeline as
    /// in the query path (ADR-0175): a row whose designated start is before the
    /// window and whose designated end is after it straddles it, so it is
    /// rendered where the bag rule would have dropped it.
    /// </summary>
    [Fact]
    public async Task A_designated_layer_renders_a_row_whose_extent_straddles_the_window()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("begins", AttributeKind.DateTimeOffset, nullable: true),
            new FieldDefinition("ends", AttributeKind.DateTimeOffset, nullable: true),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);
        Feature Event(string name, DateTimeOffset begins, DateTimeOffset ends) => new(
            new FeatureId(name),
            schema,
            [
                AttributeValue.FromString(name),
                AttributeValue.FromDateTimeOffset(begins),
                AttributeValue.FromDateTimeOffset(ends),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(0, 0, CoordinateReference.Epsg(4326))),
            ]);

        var store = new FakeStore(
            schema,
            Event("straddling", new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            Event("after", new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        var request = new MapRenderRequest(
            new RasterViewport(new Envelope(-10, -10, 10, 10), 100, 100, "EPSG:4326"),
            CircleStyle,
            [new MapLayerSource(
                "demo.places",
                store,
                new FakeCatalogue(4326, timeFields: new TemporalExtentFields("begins", "ends")),
                Time: Window(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero)))]);
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());

        var timed = (await renderer.RenderAsync(request)).Content;
        var onlyStraddling = await RenderAsync(
            new FakeStore(schema, Event("straddling", new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero))),
            null);

        Assert.Equal(onlyStraddling, timed);
    }

    /// <summary>
    /// The export path serves the three relations on a designated layer
    /// (ADR-0175 §6): a row whose extent covers the window answers
    /// <c>contains</c>, a row the window covers answers <c>within</c>, and
    /// both answer <c>overlaps</c> — so the relation is applied rather than
    /// discarded (ADR-0100's Context).
    /// </summary>
    [Fact]
    public async Task A_designated_layer_serves_contains_and_within_as_themselves()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("begins", AttributeKind.DateTimeOffset, nullable: true),
            new FieldDefinition("ends", AttributeKind.DateTimeOffset, nullable: true),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);
        Feature Event(string name, DateTimeOffset begins, DateTimeOffset ends) => new(
            new FeatureId(name),
            schema,
            [
                AttributeValue.FromString(name),
                AttributeValue.FromDateTimeOffset(begins),
                AttributeValue.FromDateTimeOffset(ends),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(0, 0, CoordinateReference.Epsg(4326))),
            ]);

        var covering = Event("covering", new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var inside = Event("inside", new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var full = new FakeStore(schema, covering, inside);
        var window = new MapTimeExtent(
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
        var designated = new FakeCatalogue(4326, timeFields: new TemporalExtentFields("begins", "ends"));

        Task<byte[]> Served(TemporalRelation relation, params Feature[] served) => RenderAsync(
            new FakeStore(schema, served), window with { Relation = relation }, designated);

        Assert.Equal(await Served(TemporalRelation.Overlaps, covering, inside), await Served(TemporalRelation.Overlaps, covering, inside));
        Assert.Equal(await Served(TemporalRelation.Overlaps, covering), await Served(TemporalRelation.Contains, covering, inside));
        Assert.Equal(await Served(TemporalRelation.Overlaps, inside), await Served(TemporalRelation.Within, covering, inside));
    }

    /// <summary>
    /// A layer with no designation has no extent to compare a window against,
    /// so the two dependent relations are refused by name rather than served as
    /// overlaps (ADR-0175 §6) — and refusing means throwing, never quietly
    /// answering with the bag rule.
    /// </summary>
    [Fact]
    public async Task A_relation_needing_an_extent_is_refused_on_an_undesignated_layer()
    {
        var store = new FakeStore(DatedSchema, Dated("inside", 0, Inside));
        var window = new MapTimeExtent(
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds())
        {
            Relation = TemporalRelation.Contains,
        };
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());

        var failure = await Assert.ThrowsAsync<SpatialException>(() => renderer.RenderAsync(Request(store, window)));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    private static Task<byte[]> RenderAsync(IFeatureStore store, MapTimeExtent? time) =>
        RenderAsync(store, time, new FakeCatalogue(4326));

    private static async Task<byte[]> RenderAsync(IFeatureStore store, MapTimeExtent? time, IDataCatalogue catalogue)
    {
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var image = await renderer.RenderAsync(new MapRenderRequest(
            new RasterViewport(new Envelope(-10, -10, 10, 10), 100, 100, "EPSG:4326"),
            CircleStyle,
            [new MapLayerSource("demo.places", store, catalogue, Time: time)]));
        return image.Content;
    }

    [Fact]
    public async Task Time_drops_features_outside_the_extent()
    {
        var full = new FakeStore(DatedSchema, Dated("inside", 0, Inside), Dated("outside", 5, Outside));
        var window = Window(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero));

        var timed = await RenderAsync(full, window);
        var reference = await RenderAsync(new FakeStore(DatedSchema, Dated("inside", 0, Inside)), null);
        var unfiltered = await RenderAsync(full, null);

        Assert.Equal(reference, timed);
        Assert.NotEqual(unfiltered, timed);
    }

    [Fact]
    public async Task Time_keeps_features_without_date_values()
    {
        var store = new FakeStore(DatelessSchema, Dateless("plain", 0));
        var window = Window(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(await RenderAsync(store, null), await RenderAsync(store, window));
    }

    [Fact]
    public async Task A_null_time_renders_everything()
    {
        var full = new FakeStore(DatedSchema, Dated("inside", 0, Inside), Dated("outside", 5, Outside));

        var rendered = await RenderAsync(full, null);
        var excluding = await RenderAsync(
            full,
            Window(new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2011, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        Assert.NotEqual(rendered, excluding);
    }

    [Fact]
    public async Task Time_filtering_honours_cancellation()
    {
        var store = new FakeStore(DatedSchema, Dated("inside", 0, Inside));
        var window = Window(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            renderer.RenderAsync(Request(store, window), new CancellationToken(canceled: true)));
    }
}
