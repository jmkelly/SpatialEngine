using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// Quality-loop pass: pin the five refactored worst offenders (RequestedIds,
/// generate-renderer Format, RoundGeometry, RenderUniqueValue) so the CRAP
/// coverage term drops alongside the reduced complexity.
/// </summary>
public sealed class QualityLoopPassTests
{
    [Theory]
    [InlineData("show:1,2", new[] { 1, 2 })]
    [InlineData("hide:1,2", new int[0])]
    [InlineData("all", new int[0])]
    [InlineData("visible", new int[0])]
    [InlineData("top", new int[0])]
    [InlineData("1,2", new[] { 1, 2 })]
    [InlineData("bogus", new int[0])]
    [InlineData("", new int[0])]
    public void RequestedIds_parses_selection_grammar(string selection, int[] expected) =>
        Assert.Equal(expected, MapServerEndpoints.RequestedIds(selection));

    [Fact]
    public void RequestedIds_is_case_insensitive_on_prefixes_and_keywords()
    {
        Assert.Equal([3], MapServerEndpoints.RequestedIds("SHOW:3"));
        Assert.Empty(MapServerEndpoints.RequestedIds("HIDE:3"));
        Assert.Empty(MapServerEndpoints.RequestedIds("ALL"));
    }

    [Theory]
    [InlineData(7L, "7")]
    [InlineData(2.5, "2.5")]
    public void Format_numbers_invariant(object raw, string expected)
    {
        var value = raw is long l ? AttributeValue.FromInt64(l) : AttributeValue.FromDouble((double)raw);
        Assert.Equal(expected, MapGenerateRenderer.Format(value));
    }

    [Fact]
    public void Format_covers_scalars_and_falls_back_for_geometry()
    {
        Assert.Equal("a", MapGenerateRenderer.Format(AttributeValue.FromString("a")));
        Assert.Equal("true", MapGenerateRenderer.Format(AttributeValue.FromBoolean(true)));
        Assert.Equal("false", MapGenerateRenderer.Format(AttributeValue.FromBoolean(false)));
        Assert.Equal("1", MapGenerateRenderer.Format(
            AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(1))));
        Assert.Equal(Guid.Empty.ToString(), MapGenerateRenderer.Format(AttributeValue.FromGuid(Guid.Empty)));
        Assert.Equal(string.Empty, MapGenerateRenderer.Format(
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2))));
    }

    [Fact]
    public void RoundGeometry_rounds_points_lines_polygons_and_collections()
    {
        var point = (Point)FeatureProjection.RoundGeometry(GeometryFactory.CreatePoint(1.23456, 2.34567), 2);
        Assert.Equal(1.23, point.Coordinate!.Value.X, 9);
        Assert.Equal(2.35, point.Coordinate!.Value.Y, 9);

        var line = (LineString)FeatureProjection.RoundGeometry(
            GeometryFactory.CreateLineString([new Coordinate(1.234, 2.345), new Coordinate(3.456, 4.567)]), 1);
        Assert.Equal(1.2, line.Sequence.GetCoordinate(0).X, 9);

        var polygon = (Polygon)FeatureProjection.RoundGeometry(
            GeometryFactory.CreatePolygon([new Coordinate(0, 0), new Coordinate(1.234, 0), new Coordinate(0, 1.234), new Coordinate(0, 0)]), 1);
        Assert.Equal(1.2, polygon.ExteriorRing.Sequence.GetCoordinate(1).X, 9);

        var multi = (MultiPoint)FeatureProjection.RoundGeometry(
            GeometryFactory.CreateMultiPoint([GeometryFactory.CreatePoint(1.26, 2.24)], null), 1);
        Assert.Equal(1.3, multi.Points[0].Coordinate!.Value.X, 9);

        var lines = (MultiLineString)FeatureProjection.RoundGeometry(
            GeometryFactory.CreateMultiLineString(
                [GeometryFactory.CreateLineString([new Coordinate(1.26, 1), new Coordinate(2, 2)])], null), 1);
        Assert.Equal(1.3, lines.LineStrings[0].Sequence.GetCoordinate(0).X, 9);

        var polys = (MultiPolygon)FeatureProjection.RoundGeometry(
            GeometryFactory.CreateMultiPolygon(
                [GeometryFactory.CreatePolygon([new Coordinate(0, 0), new Coordinate(1.26, 0), new Coordinate(0, 1), new Coordinate(0, 0)])], null), 1);
        Assert.Equal(1.3, polys.Polygons[0].ExteriorRing.Sequence.GetCoordinate(1).X, 9);

        var collection = (GeometryCollection)FeatureProjection.RoundGeometry(
            GeometryFactory.CreateGeometryCollection([GeometryFactory.CreatePoint(1.26, 1)], null), 1);
        Assert.Equal(1.3, ((Point)collection.Geometries[0]).Coordinate!.Value.X, 9);
    }

    [Fact]
    public void RenderUniqueValue_renders_entries_and_default_symbol()
    {
        using var document = JsonDocument.Parse(
            """{"field1":"country","defaultSymbol":{"type":"esriSMS","style":"esriSMSCircle","color":[0,0,0,255]},"uniqueValueInfos":[{"value":"Germany","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[255,0,0,255],"size":10}}]}""");
        var style = MapDynamicLayers.RenderUniqueValue(document.RootElement);
        Assert.Contains("country", style);
        Assert.Contains("Germany", style);
    }

    [Theory]
    [InlineData("""{"field1":"a","field2":"b","uniqueValueInfos":[{"value":"x","symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[0,0,0,255]}}]}""")]
    [InlineData("""{"field1":"a"}""")]
    [InlineData("""{"field1":"a","uniqueValueInfos":[]}""")]
    [InlineData("""{"field1":"a","uniqueValueInfos":[{"value":"x"}]}""")]
    [InlineData("""{"field1":"a","uniqueValueInfos":[{"symbol":{"type":"esriSMS","style":"esriSMSCircle","color":[0,0,0,255]}}]}""")]
    public void RenderUniqueValue_rejects_bad_renderers(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.ThrowsAny<Exception>(() => MapDynamicLayers.RenderUniqueValue(document.RootElement));
    }
}
