using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The engine/interop → Esri error envelope mapping (spec §2.0.3): every
/// failure class maps to a typed code and HTTP status.
/// </summary>
public sealed class EsriErrorMapperTests
{
    private static async Task<(int Status, JsonElement Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return (context.Response.StatusCode, document.RootElement.Clone());
    }

    [Fact]
    public async Task An_interop_failure_keeps_its_code_and_maps_to_its_status()
    {
        var (status, body) = await ExecuteAsync(EsriErrorMapper.Map(
            EsriInteropException.Invalid("bad input")));

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal(EsriErrorCodes.InvalidParameters, body.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("bad input", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData(EsriErrorCodes.InvalidParameters, StatusCodes.Status400BadRequest)]
    [InlineData(EsriErrorCodes.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(EsriErrorCodes.ServiceUnavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(EsriErrorCodes.ServerError, StatusCodes.Status500InternalServerError)]
    public async Task Interop_codes_map_to_http_statuses(int code, int expectedStatus)
    {
        var (status, _) = await ExecuteAsync(EsriErrorMapper.Map(new EsriInteropException(code, "x")));

        Assert.Equal(expectedStatus, status);
    }

    [Theory]
    [InlineData(SpatialException.InvalidArguments, EsriErrorCodes.InvalidParameters, StatusCodes.Status400BadRequest)]
    [InlineData(SpatialException.NotFound, EsriErrorCodes.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(SpatialException.StoreUnavailable, EsriErrorCodes.ServiceUnavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData("unexpected.code", EsriErrorCodes.ServerError, StatusCodes.Status500InternalServerError)]
    public async Task Engine_failures_map_to_typed_codes(string code, int expectedCode, int expectedStatus)
    {
        var (status, body) = await ExecuteAsync(EsriErrorMapper.Map(new SpatialException(code, "engine failure")));

        Assert.Equal(expectedStatus, status);
        Assert.Equal(expectedCode, body.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Cancellation_maps_to_the_client_closed_status()
    {
        var (status, body) = await ExecuteAsync(EsriErrorMapper.Map(new OperationCanceledException()));

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, status);
        Assert.Equal(EsriErrorCodes.RequestCancelled, body.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task An_unexpected_failure_is_a_server_error()
    {
        var (status, body) = await ExecuteAsync(EsriErrorMapper.Map(new InvalidOperationException("boom")));

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Equal(EsriErrorCodes.ServerError, body.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("The operation failed.", body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void EditCodeFor_maps_each_failure_class()
    {
        Assert.Equal(EsriErrorCodes.NotFound, EsriErrorMapper.EditCodeFor(SpatialException.NotFound));
        Assert.Equal(EsriErrorCodes.InvalidParameters, EsriErrorMapper.EditCodeFor(""));
        Assert.Equal(EsriErrorCodes.InvalidParameters, EsriErrorMapper.EditCodeFor((string?)null));
        Assert.Equal(EsriErrorCodes.InvalidParameters, EsriErrorMapper.EditCodeFor(new ArgumentException("x")));
        Assert.Equal(EsriErrorCodes.RequestCancelled, EsriErrorMapper.EditCodeFor(new OperationCanceledException()));
        Assert.Equal(EsriErrorCodes.ServerError, EsriErrorMapper.EditCodeFor(new InvalidOperationException("x")));
        Assert.Equal(EsriErrorCodes.InvalidParameters, EsriErrorMapper.EditCodeFor(new EsriInteropException(EsriErrorCodes.InvalidParameters, "x")));
    }
}
