using Spatial.Core.Features;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// Quality-loop pass: pin the <c>WfsService</c> operation dispatch and
/// <c>sortBy</c> parsing extracted from <c>HandleAsync</c>/<c>ParseSortKey</c>
/// (CRAP 27.9/12.6; ParseSortKey is also a metrics high with cognitive 17).
/// </summary>
public sealed class WfsSortKeyTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    private static OgcLayer Layer(string name) =>
        new(new MapLayer($"demo.{name}", 0), name, "memory",
            new DatasetDescription($"demo.{name}", "demo", name, "geometry", 4326, "Point", 5, ["id"], Schema));

    private static IReadOnlyList<OgcLayer> Loaded() => [Layer("Cities")];

    [Theory]
    [InlineData("GetPropertyValue", "The WFS operation 'GetPropertyValue' is not supported; request GetFeature instead.")]
    [InlineData("ListStoredQueries", "The WFS operation 'ListStoredQueries' is not supported; this service exposes no stored queries.")]
    [InlineData("DescribeStoredQueries", "The WFS operation 'DescribeStoredQueries' is not supported; this service exposes no stored queries.")]
    [InlineData("CreateStoredQuery", "The WFS operation 'CreateStoredQuery' is not supported; this service exposes no stored queries.")]
    [InlineData("DropStoredQuery", "The WFS operation 'DropStoredQuery' is not supported; this service exposes no stored queries.")]
    [InlineData("Transaction", "The WFS operation 'Transaction' is not supported; the write path is the gated Esri edit verbs plus neutral ingest.")]
    [InlineData("LockFeature", "The WFS operation 'LockFeature' is not supported; the write path is the gated Esri edit verbs plus neutral ingest.")]
    [InlineData("GetFeatureWithLock", "The WFS operation 'GetFeatureWithLock' is not supported; the write path is the gated Esri edit verbs plus neutral ingest.")]
    public void UnsupportedOperationMessage_names_each_rejected_operation(string request, string expected) =>
        Assert.Equal(expected, WfsService.UnsupportedOperationMessage(request));

    [Fact]
    public void UnsupportedOperationMessage_matches_case_insensitively()
    {
        Assert.Equal(
            WfsService.UnsupportedOperationMessage("Transaction"),
            WfsService.UnsupportedOperationMessage("transaction"));
    }

    [Fact]
    public void UnsupportedOperationMessage_echoes_unknown_operations()
    {
        Assert.Equal(
            "The WFS operation 'GetMap' is not supported.",
            WfsService.UnsupportedOperationMessage("GetMap"));
    }

    [Theory]
    [InlineData("population D", "population", "D")]
    [InlineData("population+D", "population", "D")]
    [InlineData("population", "population", null)]
    [InlineData("  population\tDESC  ", "population", "DESC")]
    public void SplitSortToken_accepts_plus_and_whitespace_forms(string token, string property, string? order)
    {
        var split = WfsService.SplitSortToken(token);
        Assert.Equal(property, split.Property);
        Assert.Equal(order, split.Order);
    }

    [Fact]
    public void SplitSortToken_rejects_a_three_word_entry()
    {
        var failure = Assert.Throws<OgcServiceException>(() => WfsService.SplitSortToken("a b c"));
        Assert.Contains("sortBy", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("A", false)]
    [InlineData("asc", false)]
    [InlineData("D", true)]
    [InlineData("DESC", true)]
    [InlineData("desc", true)]
    public void ParseSortOrder_maps_every_suffix(string? order, bool descending) =>
        Assert.Equal(descending, WfsService.ParseSortOrder("population", order));

    [Fact]
    public void ParseSortOrder_rejects_unknown_suffixes()
    {
        var failure = Assert.Throws<OgcServiceException>(() => WfsService.ParseSortOrder("population", "UP"));
        Assert.Contains("'UP'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSortProperty_accepts_case_insensitive_fields()
    {
        WfsService.ValidateSortProperty("NAME A", "NAME", Loaded());
    }

    [Fact]
    public void ValidateSortProperty_rejects_empty_properties()
    {
        var failure = Assert.Throws<OgcServiceException>(() => WfsService.ValidateSortProperty("+D", string.Empty, Loaded()));
        Assert.Contains("must name a property", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSortProperty_rejects_unknown_properties()
    {
        var failure = Assert.Throws<OgcServiceException>(() => WfsService.ValidateSortProperty("nope", "nope", Loaded()));
        Assert.Contains("'nope'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSortProperty_rejects_geometry_properties()
    {
        var failure = Assert.Throws<OgcServiceException>(() => WfsService.ValidateSortProperty("geometry", "geometry", Loaded()));
        Assert.Contains("geometry", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSortProperty_rejects_an_empty_layer_selection()
    {
        var failure = Assert.Throws<OgcServiceException>(
            () => WfsService.ValidateSortProperty("name", "name", []));
        Assert.Contains("no selectable layer", failure.Message, StringComparison.OrdinalIgnoreCase);
    }
}
