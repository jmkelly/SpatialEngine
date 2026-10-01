using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// T-040: the export temporal parameters (S2 export-map/, research
/// §2). <c>time</c> reuses the query grammar, <c>timeRelation</c> reads the
/// three relations the temporal-extent model can serve and refuses the rest by
/// name — as does a layer with no designation, which has no extent to compare
/// a window against (ADR-0175 §6) — and <c>layerTimeOptions</c> carries
/// per-layer opt-out, cumulative display and (rejected) offsets.
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
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void ParseTimeRelation_reads_blank_as_no_relation(string? value, TemporalRelation? expected)
    {
        Assert.Equal(expected, MapExportTime.ParseTimeRelation(value));
    }

    [Theory]
    [InlineData("esriTimeRelationOverlaps", TemporalRelation.Overlaps)]
    [InlineData("  esriTimeRelationOverlaps  ", TemporalRelation.Overlaps)]
    [InlineData("esriTimeRelationContains", TemporalRelation.Contains)]
    [InlineData("  esriTimeRelationContains  ", TemporalRelation.Contains)]
    [InlineData("esriTimeRelationWithin", TemporalRelation.Within)]
    [InlineData("esritimerelationwithin", TemporalRelation.Within)]
    public void ParseTimeRelation_reads_the_three_relations_the_model_can_serve(
        string value, TemporalRelation expected)
    {
        Assert.Equal(expected, MapExportTime.ParseTimeRelation(value));
    }

    [Theory]
    [InlineData("esriTimeRelationDisjoint")]
    [InlineData("overlaps")]
    [InlineData("yesterday")]
    public void ParseTimeRelation_rejects_a_relation_the_engine_does_not_apply(string value)
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapExportTime.ParseTimeRelation(value));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public void ParseTimeRelation_names_the_relations_the_engine_does_apply()
    {
        var failure = Assert.Throws<EsriInteropException>(
            () => MapExportTime.ParseTimeRelation("esriTimeRelationDisjoint"));

        Assert.Contains(MapExportTime.Overlaps, failure.Message, StringComparison.Ordinal);
        Assert.Contains(MapExportTime.Contains, failure.Message, StringComparison.Ordinal);
        Assert.Contains(MapExportTime.Within, failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The relation reaches the reader rather than being parsed and discarded
    /// (ADR-0100's Context): it rides the resolved window the render contract
    /// carries, so the matcher applies the one the client named.
    /// </summary>
    [Fact]
    public void ResolveTimes_carries_the_relation_to_the_reader()
    {
        var resolved = MapExportTime.ResolveTimes(
            Layers, new EsriTimeExtent(1000, 2000), null, TemporalRelation.Within);

        Assert.NotNull(resolved);
        Assert.Equal(TemporalRelation.Within, resolved[0].Relation);
        Assert.Equal(TemporalRelation.Within, resolved[1].Relation);
    }

    [Fact]
    public void ResolveTimes_reads_the_overlaps_relation_when_the_request_names_none()
    {
        var resolved = MapExportTime.ResolveTimes(Layers, new EsriTimeExtent(1000, 2000), null, null);

        Assert.NotNull(resolved);
        Assert.Equal(TemporalRelation.Overlaps, resolved[0].Relation);
    }

    /// <summary>
    /// A layer with no designation has no extent to compare the window
    /// against, so the two dependent relations are refused by name rather than
    /// accepted and answered with the bag rule (ADR-0175 §6, ADR-0081).
    /// </summary>
    [Fact]
    public void A_relation_needing_an_extent_is_refused_on_an_undesignated_layer()
    {
        var failure = Assert.Throws<EsriInteropException>(() => MapExportTime.RequireDesignated(
            TemporalRelation.Contains, [new MapDesignation(0, "Cities", null), new MapDesignation(1, "Rivers", new TemporalExtentFields("begins", "ends"))]));

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
        Assert.Contains(MapExportTime.Contains, failure.Message, StringComparison.Ordinal);
        Assert.Contains("Cities", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_designated_layer_serves_every_relation()
    {
        MapExportTime.RequireDesignated(
            TemporalRelation.Within, [new MapDesignation(0, "Cities", new TemporalExtentFields("begins", "ends"))]);

        MapExportTime.RequireDesignated(TemporalRelation.Overlaps, [new MapDesignation(0, "Cities", null)]);
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
