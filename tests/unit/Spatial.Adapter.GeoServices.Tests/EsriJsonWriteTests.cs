using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// T-092: the writer-body JSON surface. Every <c>EsriJson.Write</c> response
/// (query, geometry service, map find/identify, image catalog) must keep its
/// exact wire shape: status, JSON content type and the writer's bytes. Pins
/// the burst-tail mitigation (bytes straight to the body, no UTF-16 string
/// roundtrip) against silent content-type or body drift.
/// </summary>
public sealed class EsriJsonWriteTests
{
    private static async Task<(int Status, string? ContentType, byte[] Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        return (context.Response.StatusCode, context.Response.ContentType, ((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task Write_keeps_status_content_type_and_writer_bytes()
    {
        var (status, contentType, body) = await ExecuteAsync(EsriJson.Write(
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("count", 8);
                writer.WriteEndObject();
            }));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("application/json; charset=utf-8", contentType);
        Assert.Equal("""{"count":8}""", Encoding.UTF8.GetString(body));
    }

    [Fact]
    public async Task Write_honours_a_non_default_status()
    {
        var (status, _, body) = await ExecuteAsync(EsriJson.Write(
            writer => writer.WriteStartObject(),
            statusCode: StatusCodes.Status201Created));

        Assert.Equal(StatusCodes.Status201Created, status);
        Assert.Equal("{", Encoding.UTF8.GetString(body));
    }
}
