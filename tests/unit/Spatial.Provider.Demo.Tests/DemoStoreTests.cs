using Spatial.Core.Features;
using Spatial.PluginSdk;

namespace Spatial.Provider.Demo.Tests;

/// <summary>
/// Behaviour of the in-process demo store (ADR-0033): catalogue listing
/// with LIKE-style patterns, dataset description, scans, bbox queries and
/// the cancellable sleep job. The store is read-only: writes and creation
/// throw <c>invalid.arguments</c>.
/// </summary>
public sealed class DemoStoreTests
{
    private readonly DemoStore _store = new();

    [Fact]
    public async Task The_catalogue_lists_all_three_datasets()
    {
        var datasets = await _store.ListAsync();

        Assert.Equal(["demo.cities", "demo.points", "demo.world_cities"], datasets.Select(summary => summary.Id).Order().ToArray());
        Assert.All(datasets, summary => Assert.Equal("geometry", summary.GeometryColumn));
        Assert.All(datasets, summary => Assert.Equal(4326, summary.Srid));
    }

    [Theory]
    [InlineData("demo.points", new[] { "demo.points" })]
    [InlineData("demo.%", new[] { "demo.points", "demo.cities", "demo.world_cities" })]
    [InlineData("demo.citie_", new[] { "demo.cities" })]
    [InlineData("missing.%", new string[0])]
    [InlineData("%", new[] { "demo.points", "demo.cities", "demo.world_cities" })]
    [InlineData("d%mo.cit%es", new[] { "demo.cities" })]
    [InlineData("demo%%cities", new[] { "demo.cities", "demo.world_cities" })]
    [InlineData("%c_t%", new[] { "demo.cities", "demo.world_cities" })]
    [InlineData("demo%xyz%", new string[0])]
    [InlineData("zzz%", new string[0])]
    [InlineData("demo%zzz", new string[0])]
    [InlineData("demo.cities%es", new string[0])]
    [InlineData("", new string[0])]
    public async Task The_catalogue_pattern_filters_like_style(string pattern, string[] expected)
    {
        var datasets = await _store.ListAsync(pattern);

        Assert.Equal(expected.Order().ToArray(), datasets.Select(summary => summary.Id).Order().ToArray());
    }

    [Fact]
    public async Task Describe_returns_the_dataset_schema()
    {
        var description = await _store.DescribeAsync("demo.points");

        Assert.Equal("demo.points", description.Id);
        Assert.Equal("geometry", description.GeometryColumn);
        Assert.Equal(["name", "value", "geometry"], description.Schema.Fields.Select(field => field.Name).ToArray());
    }

    [Fact]
    public async Task Describe_of_an_unknown_dataset_is_not_found()
    {
        var exception = await Assert.ThrowsAsync<SpatialException>(() => _store.DescribeAsync("demo.missing"));

        Assert.Equal(SpatialException.NotFound, exception.Code);
    }

    [Fact]
    public async Task The_scan_returns_all_110_grid_points()
    {
        var batches = await _store.ScanAsync("demo.points");

        var features = batches.SelectMany(batch => batch.Features).ToArray();
        Assert.Equal(110, features.Length);
        var geometry = features[0]["geometry"].GeometryValue;
        Assert.False(geometry.IsEmpty);
    }

    [Fact]
    public async Task The_world_cities_scan_returns_every_city()
    {
        var batches = await _store.ScanAsync("demo.world_cities");

        var features = batches.SelectMany(batch => batch.Features).ToArray();
        Assert.Equal(WorldCities.ExpectedCount, features.Length);
        var tokyo = features.Single(feature => feature.Id.Value == "wd-1850147");
        Assert.Equal("Tokyo", tokyo["name"].StringValue);
        Assert.Equal("JP", tokyo["country"].StringValue);
        Assert.Equal(9733276, tokyo["population"].Int64Value);
        var envelope = tokyo["geometry"].GeometryValue.Envelope;
        Assert.NotNull(envelope);
        Assert.Equal(139.69171, envelope.Value.MinX);
        Assert.Equal(35.6895, envelope.Value.MinY);
    }

    [Fact]
    public async Task The_world_cities_query_filters_by_bounding_box()
    {
        var batches = await _store.QueryAsync("demo.world_cities", new BoundingBox(-74.1, 40.7, -74.0, 40.8));

        var features = batches.SelectMany(batch => batch.Features).ToArray();
        Assert.Contains(features, feature => feature.Id.Value == "wd-5128581");
    }

    [Fact]
    public void The_world_cities_reader_skips_blank_lines()
    {
        using var reader = new StringReader("1850147\tTokyo\tJP\t9733276\t35.6895\t139.69171\n\n");

        var dataset = WorldCities.BuildFromReader(reader);

        Assert.Equal("demo.world_cities", dataset.Id);
        Assert.Single(dataset.Features);
        Assert.Equal(["name", "country", "population", "geometry"], dataset.SchemaFields.Fields.Select(field => field.Name).ToArray());
    }

    [Theory]
    [InlineData("only\tthree\tcolumns")]
    [InlineData("1\tname\tXX\tnot-a-population\t10\t20")]
    [InlineData("1\tname\tXX\t100\t95\t20")]
    [InlineData("1\tname\tXX\t100\t10\t200")]
    [InlineData("\tTokyo\tJP\t9733276\t35.6895\t139.69171")]
    public void The_world_cities_reader_rejects_malformed_rows(string row)
    {
        using var reader = new StringReader(row + "\n");

        Assert.Throws<InvalidOperationException>(() => WorldCities.BuildFromReader(reader));
    }

    [Fact]
    public async Task The_query_filters_by_bounding_box()
    {
        var batches = await _store.QueryAsync("demo.points", new BoundingBox(-5, -4, -5, -4));

        var features = batches.SelectMany(batch => batch.Features).ToArray();
        Assert.Single(features);
        Assert.Equal("point-000", features[0].Id.Value);
    }

    [Fact]
    public async Task Attribute_filters_are_rejected()
    {
        var exception = await Assert.ThrowsAsync<SpatialException>(() =>
            _store.QueryAsync("demo.points", null, "value > 1"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task Writes_and_creation_are_rejected()
    {
        var batches = await _store.ScanAsync("demo.points");
        await Assert.ThrowsAsync<SpatialException>(() => _store.WriteAsync("demo.points", batches[0]));
        await Assert.ThrowsAsync<SpatialException>(() => _store.CreateAsync("demo.points", batches[0], 4326));
    }

    [Fact]
    public async Task Sleep_reports_progress_and_returns_the_duration()
    {
        var progress = new Progress<double>();
        var seen = new List<double>();
        progress.ProgressChanged += (_, value) => seen.Add(value);

        var slept = await _store.SleepAsync(50, progress);

        Assert.Equal(50, slept);
        Assert.NotEmpty(seen);
        Assert.Equal(1.0, seen[^1]);
    }

    [Fact]
    public async Task A_negative_sleep_is_invalid()
    {
        await Assert.ThrowsAsync<SpatialException>(() => _store.SleepAsync(-1));
    }

    [Fact]
    public async Task A_cancelled_sleep_throws_operation_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => _store.SleepAsync(1000, null, cts.Token));
    }
}
