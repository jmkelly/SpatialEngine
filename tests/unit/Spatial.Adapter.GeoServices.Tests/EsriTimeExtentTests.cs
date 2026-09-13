using Microsoft.AspNetCore.Http;
using Spatial.Core.Features;
using Spatial.Interop.Esri;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// T-022: the temporal surface. The <c>time</c> parameter filters against the
/// layer's date fields (instant or extent with <c>null</c> infinity bounds);
/// a layer with no date fields ignores it, matching ArcGIS Server's treatment
/// of non-time-aware layers.
/// </summary>
public sealed class EsriTimeExtentTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("observed", AttributeKind.DateTimeOffset, nullable: true),
    ]);

    private static readonly FeatureSchema DatelessSchema = new(
    [
        new FieldDefinition("name", AttributeKind.String, nullable: true),
    ]);

    private static Feature Dated(string name, DateTimeOffset observed) => new(
        new FeatureId(name),
        Schema,
        [AttributeValue.FromString(name), AttributeValue.FromDateTimeOffset(observed)]);

    private static Feature Dateless(string name) => new(
        new FeatureId(name),
        DatelessSchema,
        [AttributeValue.FromString(name)]);

    private static async Task<EsriFeatureQuery> ParseAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return EsriFeatureQuery.Parse(await EsriRequestParameters.ReadAsync(context, CancellationToken.None), fallback: null);
    }

    private static bool Matches(EsriFeatureQuery query, Feature feature) =>
        FeatureQueryEngine.Matches(query, feature, 1, queryGeometry: null, operations: null!, CancellationToken.None);

    [Fact]
    public async Task A_time_instant_matches_the_containing_date()
    {
        var query = await ParseAsync(("time", "1700000000000"));

        Assert.True(Matches(query, Dated("a", DateTimeOffset.FromUnixTimeMilliseconds(1700000000000))));
        Assert.False(Matches(query, Dated("b", DateTimeOffset.FromUnixTimeMilliseconds(1700000000001))));
    }

    [Fact]
    public async Task A_time_extent_matches_any_date_inside_with_open_bounds()
    {
        var query = await ParseAsync(("time", "1699999999000,1700000001000"));

        Assert.True(Matches(query, Dated("a", DateTimeOffset.FromUnixTimeMilliseconds(1700000000000))));
        Assert.False(Matches(query, Dated("b", DateTimeOffset.FromUnixTimeMilliseconds(1600000000000))));

        var openStart = await ParseAsync(("time", "null,1700000001000"));
        Assert.True(Matches(openStart, Dated("a", DateTimeOffset.FromUnixTimeMilliseconds(1000000000000))));

        var openEnd = await ParseAsync(("time", "1699999999000,null"));
        Assert.True(Matches(openEnd, Dated("a", DateTimeOffset.FromUnixTimeMilliseconds(1900000000000))));
    }

    [Fact]
    public async Task Time_combines_with_the_where_clause()
    {
        var query = await ParseAsync(
            ("time", "1699999999000,1700000001000"),
            ("where", "name = 'a'"));

        Assert.True(Matches(query, Dated("a", DateTimeOffset.FromUnixTimeMilliseconds(1700000000000))));
        Assert.False(Matches(query, Dated("b", DateTimeOffset.FromUnixTimeMilliseconds(1700000000000))));
        Assert.False(Matches(query, Dated("a", DateTimeOffset.FromUnixTimeMilliseconds(1600000000000))));
    }

    [Fact]
    public async Task Time_is_a_no_op_without_date_fields()
    {
        var query = await ParseAsync(("time", "1700000000000"));

        Assert.True(Matches(query, Dateless("a")));
    }

    [Fact]
    public async Task Absent_time_matches_everything()
    {
        var query = await ParseAsync();

        Assert.True(Matches(query, Dated("a", DateTimeOffset.FromUnixTimeMilliseconds(1600000000000))));
        Assert.True(Matches(query, Dateless("b")));
    }
}
