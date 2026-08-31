using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Streams;
using Spatial.Runtime.Capabilities;

namespace Spatial.Provider.Demo.Tests;

/// <summary>
/// Catalogue browsing (plan §17.3-4 against the demo catalog): the catalogue
/// stream lists both datasets as JSON metadata items, the pattern argument
/// filters them LIKE-style, and describe returns the full schema document.
/// </summary>
public sealed class DemoCatalogueTests
{
    [Fact]
    public async Task The_catalogue_lists_both_datasets_as_json_metadata_items()
    {
        var host = DemoTestHost.Create();

        var outcome = await host.InvokeAsync(DemoCapabilities.CatalogueList);

        var handle = AssertStream(outcome, out _);
        var items = await DemoTestHost.DrainAsync(handle, host.Resources);
        Assert.Equal(2, items.Count);

        var summaries = items.Cast<string>().Select(DatasetMetadataJson.ReadSummary).ToArray();
        Assert.Equal(["demo.points", "demo.cities"], summaries.Select(summary => summary.Id).ToArray());
        Assert.All(summaries, summary => Assert.Equal("geometry", summary.GeometryColumn));
        Assert.All(summaries, summary => Assert.Equal(4326, summary.Srid));
    }

    [Theory]
    [InlineData("demo.points", new[] { "demo.points" })]
    [InlineData("demo.%", new[] { "demo.points", "demo.cities" })]
    [InlineData("demo.citie_", new[] { "demo.cities" })]
    [InlineData("missing.%", new string[0])]
    public async Task The_catalogue_pattern_filters_datasets(string pattern, string[] expectedIds)
    {
        var host = DemoTestHost.Create();

        var outcome = await host.InvokeAsync(
            DemoCapabilities.CatalogueList,
            new Dictionary<string, object?> { ["pattern"] = pattern });

        var handle = AssertStream(outcome, out _);
        var items = await DemoTestHost.DrainAsync(handle, host.Resources);
        var ids = items.Cast<string>().Select(DatasetMetadataJson.ReadSummary).Select(summary => summary.Id).ToArray();
        Assert.Equal(expectedIds, ids);
    }

    [Fact]
    public async Task Describe_returns_the_full_dataset_description()
    {
        var host = DemoTestHost.Create();

        var outcome = await host.InvokeAsync(
            DemoCapabilities.DatasetDescribe,
            new Dictionary<string, object?> { ["dataset"] = "demo.points" });

        var handle = AssertStream(outcome, out _);
        var items = await DemoTestHost.DrainAsync(handle, host.Resources);
        var description = DatasetMetadataJson.ReadDescription(Assert.Single(items.Cast<string>()));

        Assert.Equal("demo.points", description.Id);
        Assert.Equal("Point", description.GeometryType);
        Assert.Equal(110, description.EstimatedRowCount);
        Assert.Equal(["name", "value", "geometry"], description.Schema.Fields.Select(field => field.Name).ToArray());
        Assert.Contains(description.Schema.Fields, field => field.Name == "geometry" && field.Kind == Spatial.Core.Features.AttributeKind.Geometry);
    }

    [Fact]
    public async Task Describe_of_an_unknown_dataset_is_an_invalid_argument_naming_the_catalog()
    {
        var host = DemoTestHost.Create();

        var outcome = await host.InvokeAsync(
            DemoCapabilities.DatasetDescribe,
            new Dictionary<string, object?> { ["dataset"] = "public.nope" });

        Assert.False(outcome.IsSuccess);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, outcome.Error?.Kind);
        Assert.Contains("demo.points", outcome.Error?.Message);
        Assert.Contains("demo.cities", outcome.Error?.Message);
    }

    [Fact]
    public async Task A_missing_dataset_argument_is_an_invalid_argument()
    {
        var host = DemoTestHost.Create();

        var outcome = await host.InvokeAsync(DemoCapabilities.FeatureScan);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, outcome.Error?.Kind);
        Assert.Contains("dataset", outcome.Error?.Message);
    }

    internal static ResourceHandle AssertStream(CapabilityOutcome outcome, out string kind)
    {
        Assert.True(outcome.IsSuccess, outcome.Error?.Message ?? "invocation failed");
        Assert.True(outcome.TryGetValue(out var value));
        var handle = Assert.IsAssignableFrom<ResourceHandle>(value);
        kind = handle.Kind.ToString();
        return handle;
    }
}
