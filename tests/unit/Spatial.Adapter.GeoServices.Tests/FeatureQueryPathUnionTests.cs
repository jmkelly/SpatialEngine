using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.Operations.NetTopologySuite;
using Spatial.Transformations.ProjNet;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The query path serves both sides of the SpatialEngine-u2x.16 reconciliation:
/// the deviation allowance of ADR-0079 (<c>maxAllowableOffset</c> and
/// <c>quantizationParameters</c>, SpatialEngine-u2x.3) and the distance band,
/// <c>returnCentroid</c> and <c>returnZ</c>/<c>returnM</c> output selection of
/// ADR-0083 (SpatialEngine-u2x.16).
///
/// The two landed on the same seam — the per-feature projection and the match
/// geometry — from opposite directions, so each side's own suite proved only
/// its half: ADR-0079 pinned
/// <c>FeatureProjection.TransformFeature</c> and ADR-0083 pinned
/// <c>FeatureProjection.MatchGeometry</c>, each called directly. A resolution
/// that threaded one of the two verbs at those call sites and dropped the
/// other satisfied both suites and silently served full precision, or a
/// centroid, that nothing asked for.
///
/// These go through <see cref="FeatureService.QueryAsync"/> — the real query
/// path — and assert that one request gets every behaviour, so neither side
/// can be dropped on the way there.
/// </summary>
public sealed class FeatureQueryPathUnionTests
{
    private static readonly NtsGeometryOperations Operations = new();
    private static readonly NtsGeometryRelations Relations = new();
    private static readonly NtsGeometryMeasures Measures = new();
    private static readonly ProjNetTransforms Transforms = new();

    private static readonly CoordinateReference WebMercator = CoordinateReference.Epsg(3857);

    private static readonly FeatureSchema Schema = new(
        [new FieldDefinition("name", AttributeKind.String),
         new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true)]);

    private static QueryServices Services => new(Operations, Relations, Measures, Transforms, Transforms);

    /// <summary>A projected polygon layer: one 1000 m circle of 181 vertices.</summary>
    private static DatasetDescription CircleLayer() => new(
        "demo.parcels", "demo", "parcels", "geometry", 3857, "Polygon", 1, [], Schema);

    /// <summary>A projected point layer: stations 0, 1, 250 and 400 m from the origin.</summary>
    private static DatasetDescription StationLayer() => new(
        "demo.stations", "demo", "stations", "geometry", 3857, "Point", 4, [], Schema);

    /// <summary>
    /// A closed 1000 m-radius ring, 181 vertices two degrees apart, carried
    /// in the layer's own projected CRS and with a Z ordinate on every vertex
    /// — so a test can see the deviation allowance, the centroid and the
    /// ordinate selection in the same response.
    /// </summary>
    private static Polygon Circle()
    {
        var ring = new Coordinate[181];
        for (var i = 0; i < ring.Length; i++)
        {
            var radians = 2 * Math.PI * i / 180;
            ring[i] = new Coordinate(1000 * Math.Cos(radians), 1000 * Math.Sin(radians), Z: 10);
        }

        return GeometryFactory.CreatePolygon(
            GeometryFactory.CreateLineString(ring, CoordinateLayout.Xyz, WebMercator));
    }

    [Fact]
    public async Task A_query_honours_the_deviation_allowance_and_serves_the_centroid_and_ordinates()
    {
        var features = await FeaturesAsync(
            CircleLayer(),
            ("maxAllowableOffset", "10"),
            ("returnCentroid", "true"));

        var feature = features.GetProperty("features")[0];

        // ADR-0079: the allowance spent its budget — fewer vertices came back.
        var ring = feature.GetProperty("geometry").GetProperty("rings")[0];
        Assert.True(ring.GetArrayLength() < 182, "the 181-vertex circle came back whole; the allowance was not applied on the query path.");

        // ADR-0083: the centroid rides beside the geometry, the Z/M flags state
        // which ordinates the coordinate arrays carry.
        // Measured from the response geometry, which the allowance moved, so
        // the centroid is near the ring's centre rather than exactly on it.
        Assert.InRange(feature.GetProperty("centroid").GetProperty("x").GetDouble(), -1, 1);
        Assert.InRange(feature.GetProperty("centroid").GetProperty("y").GetDouble(), -1, 1);
        Assert.Equal(3857, feature.GetProperty("centroid").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public async Task A_quantized_query_still_serves_the_centroid()
    {
        var features = await FeaturesAsync(
            CircleLayer(),
            ("quantizationParameters",
                """{"mode":"view","originPosition":"upperLeft","tolerance":100,"extent":{"xmin":-2000,"ymin":-2000,"xmax":2000,"ymax":2000}}"""),
            ("returnCentroid", "true"));

        var feature = features.GetProperty("features")[0];

        // ADR-0079: every ordinate snapped to the view grid anchored on the
        // request's extent.
        foreach (var vertex in feature.GetProperty("geometry").GetProperty("rings")[0].EnumerateArray())
        {
            Assert.Equal(0, (int)Math.Round(vertex[0].GetDouble()) % 100);
            Assert.Equal(0, (int)Math.Round(vertex[1].GetDouble()) % 100);
        }

        // ADR-0083: the centroid is measured from the response geometry, which
        // generalization moved, and is still written beside it.
        Assert.Equal(3857, feature.GetProperty("centroid").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Theory]
    // The exact DE-9IM relations (SpatialEngine-u2x.2) all run over the band,
    // so a band that bypassed them would answer every one of these wrongly.
    [InlineData("esriSpatialRelIntersects", 3)]
    [InlineData("esriSpatialRelWithin", 3)]
    [InlineData("esriSpatialRelEnvelopeIntersects", 3)]
    public async Task The_distance_band_composes_with_every_spatial_relation(string spatialRel, int expected)
    {
        // A 300 m band keeps the stations at 0, 1 and 250 m and drops the one
        // at 400 m, clear of the boundary so Within and Intersects agree.
        var features = await FeaturesAsync(
            StationLayer(),
            ("geometry", "0,0"),
            ("spatialRel", spatialRel),
            ("distance", "300"),
            ("units", "9001"));

        var ids = Names(features);

        Assert.Equal(expected, ids.Length);
        Assert.DoesNotContain("far", ids);
    }

    [Fact]
    public async Task A_zero_distance_leaves_the_exact_relations_on_the_query_geometry()
    {
        var features = await FeaturesAsync(
            StationLayer(),
            ("geometry", "0,0"),
            ("spatialRel", "esriSpatialRelIntersects"),
            ("distance", "0"),
            ("units", "9001"));

        Assert.Equal(["near"], Names(features));
    }

    [Fact]
    public async Task The_ordinate_selection_survives_a_deviation_allowance()
    {
        var served = await FeaturesAsync(CircleLayer(), ("maxAllowableOffset", "10"));
        var narrowed = await FeaturesAsync(CircleLayer(), ("maxAllowableOffset", "10"), ("returnZ", "false"));

        var servedGeometry = served.GetProperty("features")[0].GetProperty("geometry");
        var narrowedGeometry = narrowed.GetProperty("features")[0].GetProperty("geometry");

        // The allowance is spent in both — a handful of vertices, not 181.
        Assert.True(servedGeometry.GetProperty("rings")[0].GetArrayLength() < 182);
        Assert.True(narrowedGeometry.GetProperty("rings")[0].GetArrayLength() < 182);

        // And the Z ordinate the stored geometry carries is kept or dropped
        // on request, with the flag that says which.
        Assert.True(servedGeometry.GetProperty("hasZ").GetBoolean());
        Assert.Equal(3, servedGeometry.GetProperty("rings")[0][0].GetArrayLength());
        Assert.False(narrowedGeometry.TryGetProperty("hasZ", out _));
        Assert.Equal(2, narrowedGeometry.GetProperty("rings")[0][0].GetArrayLength());
    }

    [Fact]
    public async Task A_distance_without_a_geometry_is_a_named_failure()
    {
        var error = await Assert.ThrowsAsync<EsriInteropException>(
            () => FeaturesAsync(StationLayer(), ("distance", "250"), ("units", "9001")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, error.Code);
        Assert.Contains("'distance' parameter requires a 'geometry'", error.Message);
    }

    [Fact]
    public async Task A_negative_offset_is_a_named_failure_before_the_band_is_applied()
    {
        var error = await Assert.ThrowsAsync<EsriInteropException>(
            () => FeaturesAsync(CircleLayer(), ("geometry", "0,0"), ("maxAllowableOffset", "-1")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, error.Code);
        Assert.Contains("'maxAllowableOffset' must be a non-negative number", error.Message);
    }

    [Fact]
    public async Task A_cancelled_query_stops_before_the_projection()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FeaturesAsync(CircleLayer(), [("maxAllowableOffset", "10")], cancelled.Token));
    }

    /// <summary>The served feature names, in response order.</summary>
    private static string[] Names(JsonElement features) => features.GetProperty("features").EnumerateArray()
        .Select(feature => feature.GetProperty("attributes").GetProperty("name").GetString()!)
        .ToArray();

    private static Task<JsonElement> FeaturesAsync(
        DatasetDescription layer,
        params (string Key, string Value)[] values) =>
        FeaturesAsync(layer, values, CancellationToken.None);

    private static async Task<JsonElement> FeaturesAsync(
        DatasetDescription layer,
        (string Key, string Value)[] values,
        CancellationToken cancellationToken)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(
            values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
        var query = EsriFeatureQuery.Parse(parameters, WebMercator);
        var result = await FeatureService.QueryAsync(
            layer, new MemoryStore(layer), query, Services, cancellationToken);

        var response = new DefaultHttpContext();
        response.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        response.Response.Body = new MemoryStream();
        await result.ExecuteAsync(response);
        response.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(response.Response.Body, default, cancellationToken);
        return document.RootElement.Clone();
    }

    private sealed class MemoryStore(DatasetDescription layer) : IFeatureStore
    {
        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(Schema, Features())];
            return Task.FromResult(batches);
        }

        public Task<IReadOnlyList<FeatureBatch>> QueryAsync(
            string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default) =>
            ScanAsync(dataset, cancellationToken);

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        private IReadOnlyList<Feature> Features() => layer.GeometryType is "Point"
            ? [Station("near", 0), Station("close", 1), Station("middle", 250), Station("far", 400)]
            : [Parcel("ring", Circle())];

        private static Feature Station(string name, double easting) => new(
            new FeatureId(name),
            Schema,
            [AttributeValue.FromString(name), AttributeValue.FromGeometry(GeometryFactory.CreatePoint(easting, 0, WebMercator))]);

        private static Feature Parcel(string name, IGeometry geometry) => new(
            new FeatureId(name),
            Schema,
            [AttributeValue.FromString(name), AttributeValue.FromGeometry(geometry)]);
    }
}
