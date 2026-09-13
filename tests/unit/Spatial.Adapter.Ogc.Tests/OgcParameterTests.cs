using System.Text;
using Microsoft.AspNetCore.Http;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// OGC request parameter parsing (ADR-0052 §3): query and form bodies merge
/// case-insensitively, missing required values raise the typed report, bbox
/// ordinates parse x-first, and the supported CRS identities carry their axis
/// order.
/// </summary>
public sealed class OgcParameterTests
{
    [Fact]
    public async Task Query_parameters_are_case_insensitive()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?SERVICE=WMS&ReQuEsT=GetMap&LAYERS=a,b&width=10");

        var parameters = await OgcParameters.ReadAsync(context, CancellationToken.None);

        Assert.Equal("WMS", parameters.RequiredService("WMS"));
        Assert.Equal("GetMap", parameters.Required("request"));
        Assert.Equal(["a", "b"], parameters.List("layers"));
        Assert.Equal("10", parameters.Get("width"));
    }

    [Fact]
    public async Task Form_post_parameters_merge_over_the_query()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?service=WMS");
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("SERVICE=WFS&REQUEST=GetFeature&typeNames=ns:roads"));

        var parameters = await OgcParameters.ReadAsync(context, CancellationToken.None);

        Assert.Equal("WFS", parameters.RequiredService("WFS"));
        Assert.Equal("GetFeature", parameters.Required("request"));
        Assert.Equal("ns:roads", parameters.Get("typenames"));
    }

    [Fact]
    public async Task A_missing_required_parameter_is_a_typed_report()
    {
        var context = new DefaultHttpContext();
        var parameters = await OgcParameters.ReadAsync(context, CancellationToken.None);

        var exception = Assert.Throws<OgcServiceException>(() => parameters.Required("bbox"));

        Assert.Equal("MissingParameterValue", exception.Code);
        Assert.Equal(StatusCodes.Status400BadRequest, exception.Status);
    }

    [Fact]
    public async Task A_wrong_service_is_rejected()
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString("?service=WPS&request=GetCapabilities");
        var parameters = await OgcParameters.ReadAsync(context, CancellationToken.None);

        var exception = Assert.Throws<OgcServiceException>(() => parameters.RequiredService("WMS"));

        Assert.Equal("InvalidParameterValue", exception.Code);
    }

    [Fact]
    public void Epsg_4326_is_latitude_first_and_crs84_is_not()
    {
        Assert.Equal(("EPSG:4326", true), OgcCrs.Resolve("EPSG:4326"));
        Assert.Equal(("EPSG:4326", true), OgcCrs.Resolve("urn:ogc:def:crs:EPSG::4326"));
        Assert.Equal(("EPSG:4326", false), OgcCrs.Resolve("CRS:84"));
        Assert.Equal(("EPSG:3857", false), OgcCrs.Resolve("EPSG:3857"));
    }

    [Fact]
    public void An_unsupported_crs_is_a_typed_report()
    {
        var exception = Assert.Throws<OgcServiceException>(() => OgcCrs.Resolve("EPSG:2154"));

        Assert.Equal("InvalidParameterValue", exception.Code);
    }

    [Fact]
    public void Bbox_parses_four_ordinates()
    {
        var bbox = OgcGeometry.ParseBbox("1.5, 2.5, 3.5, 4.5");

        Assert.Equal(1.5, bbox.MinX);
        Assert.Equal(2.5, bbox.MinY);
        Assert.Equal(3.5, bbox.MaxX);
        Assert.Equal(4.5, bbox.MaxY);
    }

    [Theory]
    [InlineData("1,2,3")]
    [InlineData("a,b,c,d")]
    [InlineData("4,3,2,1")]
    public void A_malformed_bbox_is_a_typed_report(string text)
    {
        var exception = Assert.Throws<OgcServiceException>(() => OgcGeometry.ParseBbox(text));

        Assert.Equal("InvalidParameterValue", exception.Code);
    }
}
