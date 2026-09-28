using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.Operations.NetTopologySuite;
using Spatial.Transformations.ProjNet;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The <c>returnCentroid</c> and <c>returnZ</c>/<c>returnM</c> output
/// parameters (spec §9.1.4, SpatialEngine-u2x.16). The centroid is the
/// engine's <c>IGeometryMeasures.Centroid</c> verb written beside the
/// geometry, not the envelope middle; the Z/M flags select which ordinates
/// the geometry write carries.
/// </summary>
public sealed class FeatureQueryCentroidTests
{
    private static readonly NtsGeometryOperations Operations = new();
    private static readonly NtsGeometryRelations Relations = new();
    private static readonly NtsGeometryMeasures Measures = new();
    private static readonly ProjNetTransforms Transforms = new();

    private static readonly CoordinateReference Crs4326 = CoordinateReference.Epsg(4326);

    private static readonly FeatureSchema Schema = new(
        [new FieldDefinition("name", AttributeKind.String),
         new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true)]);

    private static DatasetDescription Layer() => new(
        "demo.parcels", "demo", "parcels", "geometry", 4326, "Polygon", 2, [], Schema);

    /// <summary>An L-shaped parcel (area centroid 5/3, 5/3) stored 3D with an M ordinate.</summary>
    private static readonly IGeometry Parcel = GeometryFactory.CreatePolygon(
        GeometryFactory.CreateLineString(
            [
                new Coordinate(0, 0, Z: 100, M: 7),
                new Coordinate(4, 0, Z: 100, M: 7),
                new Coordinate(4, 2, Z: 100, M: 7),
                new Coordinate(2, 2, Z: 100, M: 7),
                new Coordinate(2, 4, Z: 100, M: 7),
                new Coordinate(0, 4, Z: 100, M: 7),
                new Coordinate(0, 0, Z: 100, M: 7),
            ],
            CoordinateLayout.Xyzm,
            Crs4326));

    [Fact]
    public async Task returnCentroid_writes_the_area_centroid_beside_the_geometry()
    {
        var element = await FeaturesAsync(("returnCentroid", "true"));
        var feature = element.GetProperty("features")[0];

        Assert.Equal(5.0 / 3.0, feature.GetProperty("centroid").GetProperty("x").GetDouble(), 9);
        Assert.Equal(4326, feature.GetProperty("centroid").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public async Task returnZ_and_returnM_default_to_serving_the_stored_ordinates()
    {
        var feature = (await FeaturesAsync()).GetProperty("features")[0];
        var ring = feature.GetProperty("geometry").GetProperty("rings")[0][0];

        Assert.Equal(4, ring.GetArrayLength());
        Assert.Equal(100, ring[2].GetDouble());
        Assert.True(feature.GetProperty("geometry").GetProperty("hasZ").GetBoolean());
        Assert.True(feature.GetProperty("geometry").GetProperty("hasM").GetBoolean());
    }

    [Fact]
    public async Task returnZ_false_serves_2d_with_its_m_ordinate()
    {
        var feature = (await FeaturesAsync(("returnZ", "false"))).GetProperty("features")[0];
        var ring = feature.GetProperty("geometry").GetProperty("rings")[0][0];

        Assert.Equal(3, ring.GetArrayLength());
        Assert.Equal(7, ring[2].GetDouble());
        Assert.False(feature.GetProperty("geometry").TryGetProperty("hasZ", out _));
    }

    [Fact]
    public async Task returnM_false_serves_2d_with_its_z_ordinate()
    {
        var feature = (await FeaturesAsync(("returnM", "false"))).GetProperty("features")[0];
        var ring = feature.GetProperty("geometry").GetProperty("rings")[0][0];

        Assert.Equal(3, ring.GetArrayLength());
        Assert.Equal(100, ring[2].GetDouble());
        Assert.False(feature.GetProperty("geometry").TryGetProperty("hasM", out _));
    }

    [Fact]
    public async Task Neither_flag_means_no_centroid_and_no_ordinate_flags()
    {
        var feature = (await FeaturesAsync()).GetProperty("features")[0];

        Assert.False(feature.TryGetProperty("centroid", out _));
    }

    [Fact]
    public async Task A_centroid_request_without_geometry_output_is_a_named_failure()
    {
        var error = await Assert.ThrowsAsync<EsriInteropException>(
            () => FeaturesAsync(("returnCentroid", "true"), ("returnGeometry", "false")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, error.Code);
        Assert.Contains("'returnCentroid' parameter requires 'returnGeometry=true'", error.Message);
    }

    [Fact]
    public async Task A_centroid_request_on_a_featureless_result_shape_is_a_named_failure()
    {
        var error = await Assert.ThrowsAsync<EsriInteropException>(
            () => FeaturesAsync(("returnCentroid", "true"), ("returnIdsOnly", "true")));

        Assert.Contains("carries no features", error.Message);
    }

    [Fact]
    public async Task A_centroid_comes_back_null_for_a_feature_without_geometry()
    {
        var element = await FeaturesAsync(("returnCentroid", "true"), ("where", "name = 'empty'"));
        var feature = element.GetProperty("features")[0];

        Assert.Equal(JsonValueKind.Null, feature.GetProperty("centroid").ValueKind);
    }

    private static async Task<JsonElement> FeaturesAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(
            values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        var parameters = await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
        var query = EsriFeatureQuery.Parse(parameters, Crs4326);
        var dataset = Layer();
        var services = new QueryServices(Operations, Relations, Measures, Transforms, Transforms);
        var result = await FeatureService.QueryAsync(dataset, new ParcelStore(), query, services, CancellationToken.None);
        return await BodyAsync(result);
    }

    private static async Task<JsonElement> BodyAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private sealed class ParcelStore : IFeatureStore
    {
        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FeatureBatch> batches =
            [
                new FeatureBatch(Schema, [new Feature(new FeatureId("l"), Schema, [AttributeValue.FromString("l"), AttributeValue.FromGeometry(Parcel)])]),
                new FeatureBatch(Schema, [new Feature(new FeatureId("empty"), Schema, [AttributeValue.FromString("empty"), AttributeValue.Null])]),
            ];
            return Task.FromResult(batches);
        }

        public Task<FeatureQueryPage> QueryAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FeatureQueryPage(ScanAsync(dataset, cancellationToken).Result));

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
