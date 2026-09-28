using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.Operations.NetTopologySuite;
using Spatial.Transformations.ProjNet;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The <c>distance</c>/<c>units</c> query band and <c>returnCentroid</c>
/// (spec §9.1.4, SpatialEngine-u2x.16): the two "the engine has no verb"
/// rejects that were really engine verbs wearing a disguise.
/// <c>distance</c> is served as the query geometry buffered by the
/// requested band in the layer's own CRS units — the same
/// transform-then-buffer the Geometry Service uses — and
/// <c>returnCentroid</c> as the new <c>IGeometryMeasures.Centroid</c> verb
/// written beside each feature's geometry.
///
/// The fixture is a Web-Mercator point layer so the band is a real metric
/// distance: points at 100 m, 250 m and 400 m from the query point.
/// </summary>
public sealed class FeatureQueryDistanceTests
{
    private static readonly NtsGeometryOperations Operations = new();
    private static readonly NtsGeometryRelations Relations = new();
    private static readonly NtsGeometryMeasures Measures = new();
    private static readonly ProjNetTransforms Transforms = new();

    private static readonly FeatureSchema Schema = new(
        [new FieldDefinition("name", AttributeKind.String),
         new FieldDefinition("geometry", AttributeKind.Geometry)]);

    private static readonly CoordinateReference WebMercator = CoordinateReference.Epsg(3857);

    private static DatasetDescription Layer(int srid = 3857) => new(
        "demo.stations", "demo", "stations", "geometry", srid, "Point", 3, [], Schema);

    /// <summary>Four points along one line, at 0, 1, 250 and 400 m.</summary>
    private static IReadOnlyList<Feature> Stations(DatasetDescription layer) =>
    [
        Station("near", 0, 0, layer.Srid),
        Station("close", 1, 0, layer.Srid),
        Station("middle", 250, 0, layer.Srid),
        Station("far", 400, 0, layer.Srid),
    ];

    private static Feature Station(string name, double easting, double northing, int srid) => new(
        new FeatureId(name),
        Schema,
        [AttributeValue.FromString(name), AttributeValue.FromGeometry(GeometryFactory.CreatePoint(easting, northing, CoordinateReference.Epsg(srid)))]);

    private static QueryServices Services => new(Operations, Relations, Measures, Transforms, Transforms);

    [Theory]
    // A 100 m band keeps the points 0 m and 1 m out; 250 m adds the third;
    // 400 m keeps all four; a zero band is the exact-intersection case.
    [InlineData("100", 2)]
    [InlineData("250", 3)]
    [InlineData("400", 4)]
    [InlineData("0", 1)]
    public async Task A_distance_band_is_measured_in_the_layer_units(string distance, int expected)
    {
        var query = await ParseAsync(("geometry", "0,0"), ("distance", distance), ("units", "9001"));

        var matches = await MatchAsync(query, Layer());

        Assert.Equal(expected, matches.Count);
    }

    [Theory]
    // 9035 is the US survey mile (1609.347 m), so 1/1000 of it is 1.61 m
    // (the station 1 m out is inside) and 1/2000 is 0.80 m (it is not).
    [InlineData("0.001", 2)]
    [InlineData("0.0005", 1)]
    public async Task A_linear_unit_code_converts_the_band(string distance, int expected)
    {
        var query = await ParseAsync(("geometry", "0,0"), ("distance", distance), ("units", "9035"));

        Assert.Equal(expected, (await MatchAsync(query, Layer())).Count);
    }

    [Fact]
    public async Task A_band_composes_with_the_spatial_relation()
    {
        var query = await ParseAsync(
            ("geometry", "0,0"), ("distance", "300"), ("units", "9001"), ("spatialRel", "esriSpatialRelIntersects"));

        // The buffered band is a polygon, so the exact relation runs over it
        // rather than the point alone.
        var names = (await MatchAsync(query, Layer())).Select(match => match.Feature.Id.Value).ToArray();
        Assert.Equal(["near", "close", "middle"], names);
    }

    [Fact]
    public async Task A_geographic_layer_takes_an_angular_band()
    {
        var query = await ParseAsync(("geometry", "0,0"), ("distance", "0.001"), ("units", "9102"), ("inSR", "4326"));

        var matches = await MatchAsync(query, Layer(srid: 4326));

        Assert.Single(matches);
    }

    [Fact]
    public async Task A_distance_without_a_geometry_is_a_named_failure()
    {
        var error = await Assert.ThrowsAsync<EsriInteropException>(() => ParseAsync(("distance", "100")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, error.Code);
        Assert.Contains("'distance' parameter requires a 'geometry'", error.Message);
    }

    [Fact]
    public async Task Units_without_a_distance_is_a_named_failure()
    {
        var error = await Assert.ThrowsAsync<EsriInteropException>(
            () => ParseAsync(("geometry", "0,0"), ("units", "9001")));

        Assert.Contains("'units' parameter is only meaningful with 'distance'", error.Message);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("NaN")]
    public async Task A_malformed_distance_is_a_named_failure(string distance)
    {
        var error = await Assert.ThrowsAsync<EsriInteropException>(
            () => ParseAsync(("geometry", "0,0"), ("distance", distance)));

        Assert.Contains("'distance' parameter must be a non-negative number", error.Message);
    }

    [Fact]
    public async Task An_unknown_unit_code_names_the_curated_table()
    {
        var error = await Assert.ThrowsAsync<EsriInteropException>(
            () => ParseAsync(("geometry", "0,0"), ("distance", "1"), ("units", "12345")));

        Assert.Contains("curated unit table", error.Message);
    }

    [Fact]
    public async Task A_linear_unit_on_a_geographic_layer_names_the_reason()
    {
        var query = await ParseAsync(("geometry", "0,0"), ("distance", "1"), ("units", "9001"), ("inSR", "4326"));

        var error = await Assert.ThrowsAsync<EsriInteropException>(() => MatchAsync(query, Layer(srid: 4326)));

        Assert.Contains("the planar engine cannot buffer metres in degrees", error.Message);
    }

    [Fact]
    public async Task An_angular_unit_on_a_projected_layer_names_the_reason()
    {
        var query = await ParseAsync(("geometry", "0,0"), ("distance", "1"), ("units", "9102"));

        var error = await Assert.ThrowsAsync<EsriInteropException>(() => MatchAsync(query, Layer()));

        Assert.Contains("linear unit with a projected", error.Message);
    }

    [Fact]
    public async Task A_cancelled_match_stops_inside_the_band()
    {
        var query = await ParseAsync(("geometry", "0,0"), ("distance", "300"), ("units", "9001"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => MatchAsync(query, Layer(), cancelled.Token));
    }

    private static async Task<EsriFeatureQuery> ParseAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(
            values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        var parameters = await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
        return EsriFeatureQuery.Parse(parameters, fallback: WebMercator);
    }

    private static async Task<List<MatchedFeature>> MatchAsync(
        EsriFeatureQuery query, DatasetDescription layer, CancellationToken cancellationToken = default)
    {
        var services = Services;
        var layerCrs = EsriLayerModel.LayerCoordinateReference(layer.Srid);
        var queryGeometry = FeatureProjection.MatchGeometry(
            new FeatureProjection.QueryGeometryRequest(query, layerCrs, services), cancellationToken);
        return await FeatureSpatialMatcher.MatchAsync(
            new FeatureSpatialMatcher.QuerySpec(layer, new StationStore(layer), query, queryGeometry, services, EsriObjectIdScheme.For(layer)),
            cancellationToken);
    }

    private sealed class StationStore(DatasetDescription layer) : IFeatureStore
    {
        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(Schema, Stations(layer))];
            return Task.FromResult(batches);
        }

        public Task<IReadOnlyList<FeatureBatch>> QueryAsync(string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default) =>
            ScanAsync(dataset, cancellationToken);

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
