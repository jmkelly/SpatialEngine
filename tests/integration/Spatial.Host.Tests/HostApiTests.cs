using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Spatial.Client;
using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.Core.Geometry;

namespace Spatial.Host.Tests;

/// <summary>
/// The typed host API over the in-process services (ADR-0033): geometry,
/// transforms, demo catalogue/features/sleep and PostGIS-unconfigured
/// behaviour, driven through the .NET client SDK against the real host.
/// </summary>
public sealed class HostApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HostApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private SpatialClient Client => new(_factory.CreateClient());

    [Fact]
    public async Task Buffer_round_trips_through_the_host()
    {
        var result = await Client.BufferAsync(GeometryFactory.CreatePoint(0, 0), 1.0);

        var envelope = result.Envelope!.Value;
        Assert.Equal(-1, envelope.MinX, 1e-6);
        Assert.Equal(1, envelope.MaxX, 1e-6);
    }

    [Fact]
    public async Task A_nan_distance_is_a_400_invalid_argument()
    {
        var exception = await Assert.ThrowsAsync<SpatialClientException>(() =>
            Client.BufferAsync(GeometryFactory.CreatePoint(0, 0), double.NaN));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("invalid.arguments", exception.Code);
    }

    [Fact]
    public async Task Intersection_validate_and_simplify_serve()
    {
        var overlap = await Client.IntersectionAsync(
            GeometryFactory.CreatePolygon(
                [new Coordinate(0, 0), new Coordinate(2, 0), new Coordinate(2, 2), new Coordinate(0, 2), new Coordinate(0, 0)]),
            GeometryFactory.CreatePolygon(
                [new Coordinate(1, 1), new Coordinate(3, 1), new Coordinate(3, 3), new Coordinate(1, 3), new Coordinate(1, 1)]));
        Assert.Equal(1, overlap.Envelope!.Value.MinX, 1e-6);

        Assert.True(await Client.ValidateAsync(GeometryFactory.CreatePoint(0, 0)));

        var simplified = await Client.SimplifyAsync(
            GeometryFactory.CreateLineString(
                [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(2, 2)]),
            0.5);
        Assert.Equal(2, simplified.CoordinateCount);
    }

    [Fact]
    public async Task Describe_and_transform_serve()
    {
        var description = await Client.DescribeAsync("EPSG:4326");

        Assert.Equal("4326", description.Code);

        var transformed = await Client.TransformAsync(
            GeometryFactory.CreatePoint(13.405, 52.52, CoordinateReference.Epsg(4326)), null, "EPSG:32632");
        Assert.Equal(CoordinateReference.Epsg(32632), transformed.CoordinateReference);
    }

    [Fact]
    public async Task Demo_catalogue_scan_and_query_serve()
    {
        var datasets = await Client.ListCatalogueAsync();

        Assert.Contains(datasets, summary => summary.Id == "demo.points");

        var description = await Client.DescribeDatasetAsync("demo.points");
        Assert.Equal("geometry", description.GeometryColumn);

        var batches = await Client.ScanAsync("demo.points");
        Assert.Equal(110, batches.SelectMany(batch => batch.Features).Count());

        var window = await Client.QueryAsync("demo.points", new PluginSdk.BoundingBox(-5, -4, -5, -4));
        Assert.Single(window.SelectMany(batch => batch.Features));

        var missing = await Assert.ThrowsAsync<SpatialClientException>(() => Client.ScanAsync("demo.missing"));
        Assert.Equal(404, missing.StatusCode);
    }

    [Fact]
    public async Task Demo_sleep_completes_and_cancels()
    {
        Assert.Equal(20, await Client.SleepAsync(20));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => Client.SleepAsync(10_000, cts.Token));
    }

    [Fact]
    public async Task Demo_writes_are_rejected_and_postgis_is_unconfigured()
    {
        var batch = new FeatureBatch(new FeatureSchema([new FieldDefinition("v", AttributeKind.Double)]), []);
        var write = await Assert.ThrowsAsync<SpatialClientException>(() => Client.WriteAsync("demo.points", batch, store: "demo"));
        Assert.Equal(400, write.StatusCode);

        var catalogue = await Assert.ThrowsAsync<SpatialClientException>(() => Client.ListCatalogueAsync("postgis"));
        Assert.Equal(503, catalogue.StatusCode);
        Assert.Equal("store.unavailable", catalogue.Code);
    }

    [Fact]
    public async Task An_unknown_store_is_a_400()
    {
        var exception = await Assert.ThrowsAsync<SpatialClientException>(() => Client.ListCatalogueAsync("arcgis"));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("invalid.arguments", exception.Code);
    }

    [Fact]
    public async Task Demo_transactions_are_rejected_without_a_store()
    {
        var exception = await Assert.ThrowsAsync<SpatialClientException>(() => Client.BeginTransactionAsync("demo"));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("invalid.arguments", exception.Code);
    }

    [Fact]
    public async Task Transactions_on_an_unknown_store_are_a_400()
    {
        var exception = await Assert.ThrowsAsync<SpatialClientException>(() => Client.BeginTransactionAsync("arcgis"));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("invalid.arguments", exception.Code);
    }

    [Fact]
    public async Task Transactions_on_unconfigured_postgis_are_unavailable()
    {
        var exception = await Assert.ThrowsAsync<SpatialClientException>(() => Client.BeginTransactionAsync());

        Assert.Equal(503, exception.StatusCode);
        Assert.Equal("store.unavailable", exception.Code);
    }

    [Fact]
    public async Task Describing_an_unknown_crs_is_a_400()
    {
        var exception = await Assert.ThrowsAsync<SpatialClientException>(() => Client.DescribeAsync("EPSG:999999"));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("invalid.arguments", exception.Code);
    }

    [Fact]
    public async Task Feature_batches_decode_as_canonical_bytes()
    {
        var batches = await Client.ScanAsync("demo.cities");

        var schema = batches[0].Schema;
        Assert.Contains(schema.Fields.Select(field => field.Name), name => name == "geometry");
        var roundTripped = FeatureBatchCodec.Decode(FeatureBatchCodec.Encode(batches[0]));
        Assert.Equal(batches[0], roundTripped);
    }
}
