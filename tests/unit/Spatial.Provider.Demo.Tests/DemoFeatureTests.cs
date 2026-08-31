using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.Demo.Tests;

/// <summary>
/// Feature scans and bbox queries (plan §17.5-6 against the demo catalog):
/// the scan streams every feature as canonical batches whose geometry
/// attributes round-trip through the SGEOM codec, and the query filters by
/// an all-or-none bounding box — the exact interchange the workbench's map
/// and selection read.
/// </summary>
public sealed class DemoFeatureTests
{
    [Fact]
    public async Task The_scan_streams_all_110_grid_points_as_canonical_batches()
    {
        var host = DemoTestHost.Create();

        var outcome = await host.InvokeAsync(
            DemoCapabilities.FeatureScan,
            new Dictionary<string, object?> { ["dataset"] = "demo.points" });

        var handle = DemoCatalogueTests.AssertStream(outcome, out _);
        var batches = await DemoTestHost.DrainBytesAsync(handle, host.Resources);
        Assert.NotEmpty(batches);

        var features = batches.SelectMany(batch => FeatureBatchCodec.Decode(batch).Features).ToArray();
        Assert.Equal(110, features.Length);

        var schema = FeatureBatchCodec.Decode(batches[0]).Schema;
        Assert.Equal(["name", "value", "geometry"], schema.Fields.Select(field => field.Name).ToArray());

        // Geometry attributes are canonical SGEOM bytes: decode the first
        // feature's point and check its coordinates and CRS identity.
        var geometry = AssertGeometry(features[0]);
        Assert.False(geometry.IsEmpty);
        Assert.Equal(-5.0, geometry.Envelope?.MinX);
        Assert.Equal(-4.0, geometry.Envelope?.MinY);
        Assert.Equal("EPSG:4326", geometry.CoordinateReference?.ToString());

        Assert.Equal("point-000", features[0].Id.ToString());
        Assert.Equal("point-109", features[^1].Id.ToString());
    }

    [Fact]
    public async Task The_query_filters_by_an_all_or_none_bounding_box()
    {
        var host = DemoTestHost.Create();

        var outcome = await host.InvokeAsync(
            DemoCapabilities.FeatureQuery,
            new Dictionary<string, object?>
            {
                ["dataset"] = "demo.points",
                ["minx"] = 0.0,
                ["miny"] = 0.0,
                ["maxx"] = 2.0,
                ["maxy"] = 2.0,
            });

        var handle = DemoCatalogueTests.AssertStream(outcome, out _);
        var batches = await DemoTestHost.DrainBytesAsync(handle, host.Resources);
        var features = batches.SelectMany(batch => FeatureBatchCodec.Decode(batch).Features).ToArray();

        // x in [0,2] and y in [0,2]: w = 3 columns (0,1,2), h = 3 rows (0,1,2).
        Assert.Equal(9, features.Length);
        Assert.All(features, feature =>
        {
            var envelope = AssertGeometry(feature).Envelope!.Value;
            Assert.InRange(envelope.MinX, 0.0, 2.0);
            Assert.InRange(envelope.MinY, 0.0, 2.0);
        });
    }

    [Fact]
    public async Task The_query_without_a_bbox_streams_every_feature()
    {
        var host = DemoTestHost.Create();

        var outcome = await host.InvokeAsync(
            DemoCapabilities.FeatureQuery,
            new Dictionary<string, object?> { ["dataset"] = "demo.cities" });

        var handle = DemoCatalogueTests.AssertStream(outcome, out _);
        var batches = await DemoTestHost.DrainBytesAsync(handle, host.Resources);
        var features = batches.SelectMany(batch => FeatureBatchCodec.Decode(batch).Features).ToArray();
        Assert.Equal(8, features.Length);
    }

    [Fact]
    public async Task A_partial_bbox_is_an_invalid_argument()
    {
        var host = DemoTestHost.Create();

        var outcome = await host.InvokeAsync(
            DemoCapabilities.FeatureQuery,
            new Dictionary<string, object?>
            {
                ["dataset"] = "demo.points",
                ["minx"] = 1.0,
            });

        Assert.False(outcome.IsSuccess);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, outcome.Error?.Kind);
        Assert.Contains("bounding box", outcome.Error?.Message);
    }

    [Fact]
    public async Task An_inverted_bbox_is_an_invalid_argument()
    {
        var host = DemoTestHost.Create();

        var outcome = await host.InvokeAsync(
            DemoCapabilities.FeatureQuery,
            new Dictionary<string, object?>
            {
                ["dataset"] = "demo.points",
                ["minx"] = 3.0,
                ["miny"] = 0.0,
                ["maxx"] = 1.0,
                ["maxy"] = 2.0,
            });

        Assert.False(outcome.IsSuccess);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, outcome.Error?.Kind);
    }

    [Fact]
    public async Task An_attribute_filter_is_not_supported_and_named_as_such()
    {
        var host = DemoTestHost.Create();

        var outcome = await host.InvokeAsync(
            DemoCapabilities.FeatureQuery,
            new Dictionary<string, object?>
            {
                ["dataset"] = "demo.points",
                ["filter"] = "value > 1",
            });

        Assert.False(outcome.IsSuccess);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, outcome.Error?.Kind);
        Assert.Contains("bounding-box", outcome.Error?.Message);
    }

    [Fact]
    public async Task A_pre_cancelled_scan_fails_with_a_cancelled_error()
    {
        var host = DemoTestHost.Create();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var outcome = await host.InvokeAsync(
            DemoCapabilities.FeatureScan,
            new Dictionary<string, object?> { ["dataset"] = "demo.points" },
            cancelled.Token);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(CapabilityErrorKind.Cancelled, outcome.Error?.Kind);
    }

    /// <summary>Returns the feature's geometry attribute, decoded by the batch codec.</summary>
    private static IGeometry AssertGeometry(Feature feature)
    {
        for (var i = 0; i < feature.Schema.Count; i++)
        {
            var value = feature[i];
            if (value.Kind == Spatial.Core.Features.AttributeKind.Geometry)
            {
                return value.GeometryValue;
            }
        }

        throw new Xunit.Sdk.XunitException($"feature {feature.Id} carries no geometry attribute");
    }
}
