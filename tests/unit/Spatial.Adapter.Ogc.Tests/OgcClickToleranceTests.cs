namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// The GetFeatureInfo click tolerance (ADR-0053 §3): the widest circle
/// marker radius in a persisted MapLibre style fragment, so a click inside a
/// rendered point symbol still identifies its feature.
/// </summary>
public sealed class OgcClickToleranceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"circle\"}")]
    [InlineData("[{\"type\":\"line\",\"paint\":{\"line-width\":4}}]")]
    [InlineData("[{\"type\":\"fill\",\"paint\":{\"fill-color\":\"#fff\"}}]")]
    public void A_style_without_a_circle_marker_has_no_radius(string? style)
    {
        Assert.Equal(0, OgcClickTolerance.MarkerRadiusPixels(style));
    }

    [Fact]
    public void A_circle_marker_contributes_its_radius()
    {
        const string Style =
            """[{"type":"circle","paint":{"circle-color":"#ff0000","circle-radius":8}}]""";

        Assert.Equal(8, OgcClickTolerance.MarkerRadiusPixels(Style));
    }

    [Fact]
    public void A_circle_marker_contributes_its_stroke_half_width()
    {
        const string Style =
            """[{"type":"circle","paint":{"circle-radius":5,"circle-stroke-width":2}}]""";

        Assert.Equal(6, OgcClickTolerance.MarkerRadiusPixels(Style));
    }

    [Fact]
    public void The_widest_circle_marker_wins()
    {
        const string Style =
            """
            [
              {"type":"circle","paint":{"circle-radius":3}},
              {"type":"circle","paint":{"circle-radius":9,"circle-stroke-width":1}}
            ]
            """;

        Assert.Equal(9.5, OgcClickTolerance.MarkerRadiusPixels(Style));
    }

    [Fact]
    public void A_non_numeric_or_negative_radius_is_ignored()
    {
        const string Style =
            """
            [
              {"type":"circle","paint":{"circle-radius":["interpolate",["linear"],["zoom"],0,1,10,20]}},
              {"type":"circle","paint":{"circle-radius":-4,"circle-stroke-width":-2}}
            ]
            """;

        Assert.Equal(0, OgcClickTolerance.MarkerRadiusPixels(Style));
    }

    [Theory]
    [InlineData("[42]")]
    [InlineData("[{\"type\":7,\"paint\":{\"circle-radius\":4}}]")]
    [InlineData("[{\"type\":\"circle\",\"paint\":42}]")]
    public void Malformed_fragments_are_ignored(string style)
    {
        Assert.Equal(0, OgcClickTolerance.MarkerRadiusPixels(style));
    }
}
