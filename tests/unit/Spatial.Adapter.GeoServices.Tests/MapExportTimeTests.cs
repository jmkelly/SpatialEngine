using Spatial.Esri.Codec;
using Spatial.PluginSdk;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// T-040: the export temporal parameters (S2 export-map/, research
/// §2). <c>time</c> reuses the query grammar, <c>timeRelation</c> accepts
/// the documented relations (equivalent for the engine's instant date
/// values), and <c>layerTimeOptions</c> carries per-layer opt-out,
/// cumulative display and (rejected) offsets.
/// </summary>
public sealed class MapExportTimeTests
{
    private static readonly IReadOnlyList<PublishedLayer> Layers =
    [
        new(0, "demo.cities", "Cities"),
        new(1, "demo.rivers", "Rivers"),
    ];

    [Theory]
    [InlineData(null, null)]
    [InlineData("esriTimeRelationOverlaps", "esriTimeRelationOverlaps")]
    [InlineData("esriTimeRelationContains", "esriTimeRelationContains")]
    [InlineData("esriTimeRelationWithin", "esriTimeRelationWithin")]
    public void ParseTimeRelation_accepts_blank_and_the_documented_relations(string? value, string? expected)
    {
        Assert.Equal(expected, MapExportTime.ParseTimeRelation(value));
    }

    [Theory]
    [InlineData("overlaps")]
    [InlineData("esriTimeRelationDisjoint")]
    [InlineData("yesterday")]
    public void ParseTimeRelation_rejects_unknown_relations(string value)
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapExportTime.ParseTimeRelation(value));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseLayerTimeOptions_returns_null_without_input(string? value)
    {
        Assert.Null(MapExportTime.ParseLayerTimeOptions(value));
    }

    [Fact]
    public void ParseLayerTimeOptions_reads_per_layer_use_time()
    {
        var options = MapExportTime.ParseLayerTimeOptions("""[{"id":0,"useTime":false},{"id":"1","useTime":true}]""");

        Assert.NotNull(options);
        Assert.False(options[0].UseTime);
        Assert.True(options[1].UseTime);
    }

    [Fact]
    public void ParseLayerTimeOptions_defaults_to_use_time()
    {
        var options = MapExportTime.ParseLayerTimeOptions("""[{"id":0}]""");

        Assert.NotNull(options);
        Assert.True(options[0].UseTime);
        Assert.False(options[0].Cumulative);
    }

    [Fact]
    public void ParseLayerTimeOptions_reads_cumulative_display()
    {
        var options = MapExportTime.ParseLayerTimeOptions("""[{"id":0,"useTime":true,"timeDataCumulative":true}]""");

        Assert.NotNull(options);
        Assert.True(options[0].Cumulative);
    }

    [Fact]
    public void ParseLayerTimeOptions_accepts_a_zero_offset_as_a_no_op()
    {
        var options = MapExportTime.ParseLayerTimeOptions("""[{"id":0,"timeOffset":0,"timeOffsetUnits":"esriTimeUnitsDays"}]""");

        Assert.NotNull(options);
        Assert.True(options[0].UseTime);
    }

    [Fact]
    public void ParseLayerTimeOptions_rejects_a_non_zero_offset()
    {
        var failure = Assert.Throws<EsriInteropException>(() =>
            MapExportTime.ParseLayerTimeOptions("""[{"id":0,"timeOffset":5,"timeOffsetUnits":"esriTimeUnitsDays"}]"""));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"id":0}""")]
    [InlineData("[1,2]")]
    [InlineData("""[{"useTime":true}]""")]
    [InlineData("""[{"id":"roads","useTime":true}]""")]
    public void ParseLayerTimeOptions_rejects_malformed_input(string value)
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapExportTime.ParseLayerTimeOptions(value));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public void ResolveTimes_returns_null_without_a_time_extent()
    {
        Assert.Null(MapExportTime.ResolveTimes(Layers, null, null));
    }

    [Fact]
    public void ResolveTimes_applies_the_extent_to_opted_in_layers()
    {
        var time = new EsriTimeExtent(1000, 2000);

        var resolved = MapExportTime.ResolveTimes(Layers, time, null);

        Assert.NotNull(resolved);
        Assert.Equal(new MapTimeExtent(1000, 2000), resolved[0]);
        Assert.Equal(new MapTimeExtent(1000, 2000), resolved[1]);
    }

    [Fact]
    public void ResolveTimes_skips_opted_out_layers()
    {
        var time = new EsriTimeExtent(1000, 2000);
        var options = MapExportTime.ParseLayerTimeOptions("""[{"id":1,"useTime":false}]""");

        var resolved = MapExportTime.ResolveTimes(Layers, time, options);

        Assert.NotNull(resolved);
        Assert.Single(resolved);
        Assert.Equal(new MapTimeExtent(1000, 2000), resolved[0]);
    }

    [Fact]
    public void ResolveTimes_opens_the_start_for_cumulative_layers()
    {
        var time = new EsriTimeExtent(1000, 2000);
        var options = MapExportTime.ParseLayerTimeOptions("""[{"id":0,"timeDataCumulative":true}]""");

        var resolved = MapExportTime.ResolveTimes(Layers, time, options);

        Assert.NotNull(resolved);
        Assert.Equal(new MapTimeExtent(null, 2000), resolved[0]);
        Assert.Equal(new MapTimeExtent(1000, 2000), resolved[1]);
    }
}
