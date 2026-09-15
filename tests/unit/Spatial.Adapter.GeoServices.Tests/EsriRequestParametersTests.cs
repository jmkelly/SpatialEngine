using System.Text;
using Microsoft.AspNetCore.Http;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The merged query/form/JSON parameter reader: GeoServices passes every
/// argument as a named string regardless of verb.
/// </summary>
public sealed class EsriRequestParametersTests
{
    private static async Task<EsriRequestParameters> ReadAsync(Action<HttpContext> configure)
    {
        var context = new DefaultHttpContext();
        configure(context);
        return await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
    }

    private static void Body(HttpContext context, string contentType, string content)
    {
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    [Fact]
    public async Task Query_string_values_are_read()
    {
        var parameters = await ReadAsync(context => context.Request.QueryString = new QueryString("?f=json&where=name%20%3D%20%27x%27"));

        Assert.Equal("json", parameters.Get("f"));
        Assert.Equal("name = 'x'", parameters.Get("where"));
        Assert.Null(parameters.Get("missing"));
        Assert.True(parameters.Has("f"));
        Assert.False(parameters.Has("missing"));
    }

    [Fact]
    public async Task Post_form_values_are_merged_over_the_query()
    {
        var parameters = await ReadAsync(context =>
        {
            context.Request.QueryString = new QueryString("?f=json");
            Body(context, "application/x-www-form-urlencoded", "f=html&objectIds=1%2C2");
        });

        Assert.Equal("html", parameters.Get("f"));
        Assert.Equal("1,2", parameters.Get("objectIds"));
    }

    [Fact]
    public async Task Post_json_values_are_read()
    {
        var parameters = await ReadAsync(context => Body(context, "application/json", """{"features":[{"attributes":{"name":"x"}}],"rollbackOnFailure":true}"""));

        Assert.Equal("true", parameters.Get("rollbackOnFailure"));
        Assert.Equal("""[{"attributes":{"name":"x"}}]""", parameters.Get("features"));
    }

    [Fact]
    public async Task Post_json_with_a_non_object_root_is_ignored()
    {
        var parameters = await ReadAsync(context => Body(context, "application/json", "[1,2,3]"));

        Assert.False(parameters.Has("features"));
    }

    [Fact]
    public async Task Post_without_a_form_or_json_body_reads_only_the_query()
    {
        var parameters = await ReadAsync(context =>
        {
            context.Request.QueryString = new QueryString("?f=json");
            context.Request.Method = HttpMethods.Post;
            context.Request.ContentType = "text/plain";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("ignored"));
        });

        Assert.Equal("json", parameters.Get("f"));
    }

    [Fact]
    public async Task Get_ignores_the_body()
    {
        var parameters = await ReadAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"f":"json"}"""));
        });

        Assert.False(parameters.Has("f"));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("FALSE", false)]
    [InlineData("0", false)]
    public async Task Booleans_are_parsed(string value, bool expected)
    {
        var parameters = await ReadAsync(context => context.Request.QueryString = new QueryString($"?flag={value}"));

        Assert.Equal(expected, parameters.GetBool("flag", fallback: false));
    }

    [Fact]
    public async Task A_missing_boolean_uses_the_fallback()
    {
        var parameters = await ReadAsync(_ => { });

        Assert.True(parameters.GetBool("flag", fallback: true));
        Assert.False(parameters.GetBool("flag", fallback: false));
    }

    [Fact]
    public async Task A_malformed_boolean_is_rejected()
    {
        var parameters = await ReadAsync(context => context.Request.QueryString = new QueryString("?flag=maybe"));

        Assert.Throws<EsriInteropException>(() => parameters.GetBool("flag", fallback: false));
    }

    [Fact]
    public async Task Require_rejects_a_missing_or_blank_value()
    {
        var parameters = await ReadAsync(context => context.Request.QueryString = new QueryString("?blank=++"));

        Assert.Equal("value", (await ReadAsync(context => context.Request.QueryString = new QueryString("?name=value"))).Require("name"));
        Assert.Throws<EsriInteropException>(() => parameters.Require("missing"));
        Assert.Throws<EsriInteropException>(() => parameters.Require("blank"));
    }
}
