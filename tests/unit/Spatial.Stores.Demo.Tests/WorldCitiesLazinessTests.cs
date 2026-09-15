using System.Diagnostics;
using Spatial.Contracts;
using Xunit;

namespace Spatial.Stores.Demo.Tests;

[CollectionDefinition("DemoColdStart", DisableParallelization = true)]
public sealed class DemoColdStart
{
}

/// <summary>
/// Cold-start laziness of the world-cities snapshot (T-095): the committed
/// 34k-row CSV costs ~790ms to parse, so no catalog List and no
/// describe/scan/query of another dataset may trigger it. Only an access
/// that actually names <c>demo.world_cities</c> loads the snapshot.
/// Load-observation uses <see cref="WorldCities.IsLoaded"/> deltas (never an
/// absolute "unloaded" assert) so the tests stay deterministic no matter
/// which other tests already warmed the process-wide snapshot.
/// </summary>
[Collection("DemoColdStart")]
public sealed class WorldCitiesLazinessTests
{
    [Fact]
    public async Task List_without_a_pattern_does_not_load_the_snapshot()
    {
        var loadedBefore = WorldCities.IsLoaded;

        var summaries = await new DemoStore().ListAsync();

        Assert.Equal(loadedBefore, WorldCities.IsLoaded);
        Assert.Equal(3, summaries.Count);
        var cities = summaries.Single(summary => summary.Id == WorldCities.DatasetId);
        Assert.Equal(WorldCities.ExpectedCount, cities.EstimatedRowCount);
        Assert.Equal("geometry", cities.GeometryColumn);
        Assert.Equal(4326, cities.Srid);
    }

    [Fact]
    public async Task List_with_a_non_matching_pattern_does_not_load_the_snapshot()
    {
        var loadedBefore = WorldCities.IsLoaded;

        var summaries = await new DemoStore().ListAsync("demo.citie_");

        Assert.Equal(loadedBefore, WorldCities.IsLoaded);
        Assert.Single(summaries);
    }

    [Fact]
    public async Task Describe_of_another_dataset_does_not_load_the_snapshot()
    {
        var loadedBefore = WorldCities.IsLoaded;

        var description = await new DemoStore().DescribeAsync("demo.points");

        Assert.Equal(loadedBefore, WorldCities.IsLoaded);
        Assert.Equal("demo.points", description.Id);
    }

    [Fact]
    public async Task Scan_and_query_of_points_do_not_load_the_snapshot()
    {
        var loadedBefore = WorldCities.IsLoaded;
        var store = new DemoStore();

        var scanned = await store.ScanAsync("demo.points");
        var queried = await store.QueryAsync("demo.points", new BoundingBox(-5, -4, -5, -4));

        Assert.Equal(loadedBefore, WorldCities.IsLoaded);
        Assert.Equal(110, scanned.SelectMany(batch => batch.Features).Count());
        Assert.Single(queried.SelectMany(batch => batch.Features));
    }

    [Fact]
    public async Task Cold_list_and_points_query_stay_fast()
    {
        var store = new DemoStore();
        var stopwatch = Stopwatch.StartNew();

        await store.ListAsync();
        await store.QueryAsync("demo.points", new BoundingBox(-5, -4, -5, -4));

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(100),
            $"Cold List + layer-0 query took {stopwatch.Elapsed}; the world-cities snapshot must not parse on this path.");
    }

    [Fact]
    public async Task World_cities_access_loads_and_stays_correct()
    {
        var store = new DemoStore();

        var description = await store.DescribeAsync(WorldCities.DatasetId);
        var batches = await store.ScanAsync(WorldCities.DatasetId);

        Assert.True(WorldCities.IsLoaded);
        Assert.Equal(WorldCities.DatasetId, description.Id);
        Assert.Equal(WorldCities.ExpectedCount, description.EstimatedRowCount);
        var features = batches.SelectMany(batch => batch.Features).ToArray();
        Assert.Equal(WorldCities.ExpectedCount, features.Length);
        var tokyo = features.Single(feature => feature.Id.Value == "wd-1850147");
        Assert.Equal("Tokyo", tokyo["name"].StringValue);
    }
}
