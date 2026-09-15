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

    private static async Task<byte[]> RenderAsync(IFeatureStore store, MapTimeExtent? time)
    {
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var image = await renderer.RenderAsync(Request(store, time));
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
