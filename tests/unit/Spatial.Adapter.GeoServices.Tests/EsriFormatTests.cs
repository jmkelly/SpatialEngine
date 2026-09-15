using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// T-049 directory scope: the facade serves JSON only (ADR-0035), so
/// <c>f=html</c> — the Services Directory default — and any other non-JSON
/// <c>f</c> is a typed <c>invalid.arguments</c> failure (Esri code 400)
/// naming the JSON surface. The per-root HTTP replay lives in
/// Spatial.Host.Tests.GeoServicesCatalogHonestyTests.
/// </summary>
public sealed class EsriFormatTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("json")]
    [InlineData("JSON")]
    [InlineData("pjson")]
    [InlineData("PJSON")]
    public void Json_and_its_alias_pass(string? format)
    {
        EsriFormat.Ensure(format);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("HTML")]
    [InlineData("geojson")]
    [InlineData("pbf")]
    [InlineData("kmz")]
    public void A_non_json_format_is_a_typed_failure_naming_the_json_surface(string format)
    {
        var failure = Assert.Throws<EsriInteropException>(() => EsriFormat.Ensure(format));

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
        Assert.Contains("supportedQueryFormats", failure.Message, StringComparison.Ordinal);
        Assert.Contains("f=json", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xml")]
    [InlineData("XML")]
    public void Xml_and_an_absent_format_pass_for_metadata(string? format)
    {
        EsriFormat.EnsureXml(format);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("pjson")]
    [InlineData("html")]
    public void A_non_xml_format_is_a_typed_failure_naming_the_xml_surface(string format)
    {
        var failure = Assert.Throws<EsriInteropException>(() => EsriFormat.EnsureXml(format));

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
        Assert.Contains("f=xml", failure.Message, StringComparison.Ordinal);
    }
}
