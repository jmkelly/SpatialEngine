using Spatial.Core.Features;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// WMS text/parameter helpers (ADR-0052 §3): attribute formatting for
/// GetFeatureInfo text and BGCOLOR normalisation. Every attribute kind and
/// colour form is exercised so the dispatch table is fully covered.
/// </summary>
public sealed class WmsServiceTests
{
    [Fact]
    public void Format_value_covers_every_attribute_kind()
    {
        Assert.Equal("true", WmsService.FormatValue(AttributeValue.FromBoolean(true)));
        Assert.Equal("false", WmsService.FormatValue(AttributeValue.FromBoolean(false)));
        Assert.Equal("42", WmsService.FormatValue(AttributeValue.FromInt64(42)));
        Assert.Equal("1.5", WmsService.FormatValue(AttributeValue.FromDouble(1.5)));
        Assert.Equal("text", WmsService.FormatValue(AttributeValue.FromString("text")));
        Assert.Equal(
            "2026-09-17T00:00:00.0000000+00:00",
            WmsService.FormatValue(AttributeValue.FromDateTimeOffset(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero))));
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal(guid.ToString(), WmsService.FormatValue(AttributeValue.FromGuid(guid)));
        Assert.Equal(string.Empty, WmsService.FormatValue(AttributeValue.Null));
    }

    [Fact]
    public void Parse_color_normalises_the_documented_forms()
    {
        Assert.Null(WmsService.ParseColor(null));
        Assert.Equal("#AABBCC", WmsService.ParseColor("#aabbcc"));
        Assert.Equal("#AABBCC", WmsService.ParseColor("0xaabbcc"));
        Assert.Equal("#AABBCC", WmsService.ParseColor("aabbcc"));
        Assert.Equal("#AABBCC", WmsService.ParseColor("  #AaBbCc  "));
    }

    [Theory]
    [InlineData("#12345")]
    [InlineData("0x1234")]
    [InlineData("#12345g")]
    public void Parse_color_rejects_invalid_values(string value)
    {
        var exception = Assert.Throws<OgcServiceException>(() => WmsService.ParseColor(value));

        Assert.Equal("InvalidParameterValue", exception.Code);
    }
}
