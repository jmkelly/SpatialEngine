using System.Text.Json;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// Quality-loop pass 2: pin the refactored worst offenders (ParseOutStatistic,
/// Aggregate, ParseArrayLayerDefs, MatchesFilters/ParseLayerDef) so the CRAP
/// coverage term drops alongside the reduced complexity.
/// </summary>
public sealed class QualityLoopPass2Tests
{
    private static readonly FeatureSchema DoubleSchema = new(
    [
        new FieldDefinition("v", AttributeKind.Double, nullable: true),
    ]);

    private static readonly FeatureSchema IntSchema = new(
    [
        new FieldDefinition("n", AttributeKind.Int64, nullable: true),
    ]);

    private static EsriOutStatistic ParseStat(string json, HashSet<string>? seen = null)
    {
        using var document = JsonDocument.Parse(json);
        return EsriFeatureQuery.ParseOutStatistic(
            document.RootElement, seen ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("count", "*", "c")]
    [InlineData("sum", "pop", "total")]
    [InlineData("min", "pop", "lo")]
    [InlineData("max", "pop", "hi")]
    [InlineData("avg", "pop", "mean")]
    [InlineData("stddev", "pop", "sd")]
    [InlineData("var", "pop", "variance")]
    [InlineData("COUNT", "pop", "upper")]
    public void ParseOutStatistic_accepts_scalar_types(string type, string field, string name)
    {
        var stat = ParseStat(
            $$"""{"statisticType":"{{type}}","onStatisticField":"{{field}}","outStatisticFieldName":"{{name}}" }""");
        Assert.Equal(type.ToLowerInvariant(), stat.StatisticType);
        Assert.Equal(field, stat.OnStatisticField);
        Assert.Equal(name, stat.OutStatisticFieldName);
        Assert.Null(stat.PercentileValue);
    }

    [Fact]
    public void ParseOutStatistic_accepts_percentile_with_parameters()
    {
        var stat = ParseStat(
            """{"statisticType":"percentile_cont","onStatisticField":"pop","outStatisticFieldName":"p90","statisticParameters":{"value":0.9,"orderBy":"DESC"}}""");
        Assert.Equal("percentile_cont", stat.StatisticType);
        Assert.Equal(0.9, stat.PercentileValue);
        Assert.True(stat.PercentileDescending);
    }

    [Theory]
    [InlineData("""{"statisticType":"nope","onStatisticField":"pop","outStatisticFieldName":"x"}""")]
    [InlineData("""{"statisticType":"sum","outStatisticFieldName":"x"}""")]
    [InlineData("""{"statisticType":"","onStatisticField":"pop","outStatisticFieldName":"x"}""")]
    [InlineData("""{"statisticType":"sum","onStatisticField":"pop","outStatisticFieldName":"x","statisticParameters":{"value":1}}""")]
    [InlineData("""{"statisticType":"percentile_disc","onStatisticField":"pop","outStatisticFieldName":"x"}""")]
    [InlineData("[1]")]
    public void ParseOutStatistic_rejects_bad_entries(string json) =>
        Assert.ThrowsAny<Exception>(() => ParseStat(json));

    [Fact]
    public void ParseOutStatistic_rejects_duplicate_names()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ParseStat("""{"statisticType":"sum","onStatisticField":"pop","outStatisticFieldName":"t"}""", seen);
        Assert.ThrowsAny<Exception>(() => ParseStat(
            """{"statisticType":"min","onStatisticField":"pop","outStatisticFieldName":"T"}""", seen));
    }

    private static Feature DoubleRow(string id, double? value) => new(
        new FeatureId(id),
        DoubleSchema,
        [value is { } number ? AttributeValue.FromDouble(number) : AttributeValue.Null]);

    private static Feature IntRow(string id, long? value) => new(
        new FeatureId(id),
        IntSchema,
        [value is { } number ? AttributeValue.FromInt64(number) : AttributeValue.Null]);

    private static FeatureQueryEngine.MatchedFeature[] Members(params Feature[] features) =>
        features.Select((feature, index) => new FeatureQueryEngine.MatchedFeature(index + 1, feature)).ToArray();

    [Fact]
    public void Aggregate_counts_rows_and_non_nulls()
    {
        var members = Members(DoubleRow("a", 1), DoubleRow("b", null), DoubleRow("c", 3));
        var rows = FeatureStatisticsEngine.Aggregate(
            members, new FeatureStatisticsEngine.StatisticInput(new EsriOutStatistic("count", "*", "c"), -1, AttributeKind.Int64, true));
        Assert.Equal(3, rows.Int64Value);
        var values = FeatureStatisticsEngine.Aggregate(
            members, new FeatureStatisticsEngine.StatisticInput(new EsriOutStatistic("count", "v", "c"), 0, AttributeKind.Double, false));
        Assert.Equal(2, values.Int64Value);
    }

    [Fact]
    public void Aggregate_returns_null_when_everything_is_null()
    {
        var members = Members(DoubleRow("a", null));
        var result = FeatureStatisticsEngine.Aggregate(
            members, new FeatureStatisticsEngine.StatisticInput(new EsriOutStatistic("avg", "v", "a"), 0, AttributeKind.Double, false));
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Aggregate_reduces_min_max_and_sums()
    {
        var members = Members(DoubleRow("a", 3), DoubleRow("b", 1), DoubleRow("c", 2));
        var min = FeatureStatisticsEngine.Aggregate(
            members, new FeatureStatisticsEngine.StatisticInput(new EsriOutStatistic("min", "v", "m"), 0, AttributeKind.Double, false));
        var max = FeatureStatisticsEngine.Aggregate(
            members, new FeatureStatisticsEngine.StatisticInput(new EsriOutStatistic("max", "v", "m"), 0, AttributeKind.Double, false));
        Assert.Equal(1, min.DoubleValue);
        Assert.Equal(3, max.DoubleValue);

        var ints = Members(IntRow("a", 2), IntRow("b", 5));
        var sum = FeatureStatisticsEngine.Aggregate(
            ints, new FeatureStatisticsEngine.StatisticInput(new EsriOutStatistic("sum", "n", "s"), 0, AttributeKind.Int64, false));
        Assert.Equal(AttributeKind.Int64, sum.Kind);
        Assert.Equal(7, sum.Int64Value);

        var avg = FeatureStatisticsEngine.Aggregate(
            members, new FeatureStatisticsEngine.StatisticInput(new EsriOutStatistic("avg", "v", "a"), 0, AttributeKind.Double, false));
        Assert.Equal(2, avg.DoubleValue);
    }

    [Fact]
    public void Aggregate_computes_variance_stddev_and_percentiles()
    {
        var members = Members(DoubleRow("a", 2), DoubleRow("b", 4));
        var variance = FeatureStatisticsEngine.Aggregate(
            members, new FeatureStatisticsEngine.StatisticInput(new EsriOutStatistic("var", "v", "x"), 0, AttributeKind.Double, false));
        var stddev = FeatureStatisticsEngine.Aggregate(
            members, new FeatureStatisticsEngine.StatisticInput(new EsriOutStatistic("stddev", "v", "x"), 0, AttributeKind.Double, false));
        Assert.Equal(2, variance.DoubleValue, 9);
        Assert.Equal(Math.Sqrt(2), stddev.DoubleValue, 9);

        var quartet = Members(DoubleRow("a", 10), DoubleRow("b", 20), DoubleRow("c", 30), DoubleRow("d", 40));
        var discrete = FeatureStatisticsEngine.Aggregate(
            quartet, new FeatureStatisticsEngine.StatisticInput(new EsriOutStatistic("percentile_disc", "v", "p", 0.5, false), 0, AttributeKind.Double, false));
        var continuous = FeatureStatisticsEngine.Aggregate(
            quartet, new FeatureStatisticsEngine.StatisticInput(new EsriOutStatistic("percentile_cont", "v", "p", 0.5, false), 0, AttributeKind.Double, false));
        Assert.Equal(20, discrete.DoubleValue);
        Assert.Equal(25, continuous.DoubleValue);
    }

    [Fact]
    public void Aggregate_returns_null_for_unknown_types()
    {
        var members = Members(DoubleRow("a", 1));
        var result = FeatureStatisticsEngine.Aggregate(
            members, new FeatureStatisticsEngine.StatisticInput(new EsriOutStatistic("median", "v", "m"), 0, AttributeKind.Double, false));
        Assert.True(result.IsNull);
    }

    [Fact]
    public void ParseLayerDefs_accepts_array_syntax_with_overrides()
    {
        var defs = FeatureServiceQuery.ParseLayerDefs(
            """[{"layerId": 0, "where": "POP > 1", "outFields": "a, b"}, {"layerId": 2}]""");
        Assert.Equal(2, defs.Count);
        Assert.NotNull(defs[0].Where);
        Assert.Equal(["a", "b"], defs[0].OutFields);
        Assert.Null(defs[2].Where);
        Assert.Null(defs[2].OutFields);
    }

    [Theory]
    [InlineData("""[{"where": "POP > 1"}]""")]
    [InlineData("""[{"layerId": -1}]""")]
    [InlineData("""[{"layerId": 0, "where": ""}]""")]
    [InlineData("""[{"layerId": 0, "where": 42}]""")]
    [InlineData("""[{"layerId": 0, "outFields": ""}]""")]
    [InlineData("""[{"layerId": 0, "outFields": " , "}]""")]
    [InlineData("""[{"layerId": 0, "where": "POP > "}]""")]
    [InlineData("""[42]""")]
    [InlineData("[]")]
    public void ParseLayerDefs_rejects_bad_array_entries(string raw) =>
        Assert.ThrowsAny<Exception>(() => FeatureServiceQuery.ParseLayerDefs(raw));

    private static readonly FeatureSchema FilterSchema = new(
    [
        new FieldDefinition("pop", AttributeKind.Int64, nullable: true),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
    ]);

    private static DatasetDescription FilterLayer() => new(
        "demo.cities", "demo", "cities", "geometry", 4326, "Point", 3, ["name"], FilterSchema);

    private static Feature FilterRow(string id, long pop, string name) => new(
        new FeatureId(id),
        FilterSchema,
        [AttributeValue.FromInt64(pop), AttributeValue.FromString(name)]);

    [Fact]
    public void ParseLayerDef_round_trips_a_where_clause()
    {
        var clause = MapIdentifyEngine.ParseLayerDef(0, "pop > 1");
        Assert.NotNull(clause);
        Assert.ThrowsAny<Exception>(() => MapIdentifyEngine.ParseLayerDef(0, "pop > "));
    }

    [Fact]
    public void MatchesFilters_applies_definition_and_time()
    {
        var dataset = FilterLayer();
        var scheme = EsriObjectIdScheme.For(dataset);
        var definition = MapIdentifyEngine.ParseLayerDef(0, "pop > 100");
        Assert.True(MapIdentifyEngine.MatchesFilters(null, dataset, null, FilterRow("a", 5, "x"), 1, null));
        Assert.True(MapIdentifyEngine.MatchesFilters(scheme, dataset, definition, FilterRow("a", 500, "x"), 1, null));
        Assert.False(MapIdentifyEngine.MatchesFilters(scheme, dataset, definition, FilterRow("b", 5, "x"), 2, null));
    }
}
