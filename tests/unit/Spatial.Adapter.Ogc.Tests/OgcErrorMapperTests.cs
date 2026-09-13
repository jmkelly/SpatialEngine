using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.PluginSdk;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// The OGC failure mapping (ADR-0052 §3): every exception kind and every
/// engine <c>SpatialException</c> code produces the matching
/// <c>ServiceExceptionReport</c> status and code.
/// </summary>
public sealed class OgcErrorMapperTests
{
    [Fact]
    public async Task An_adapter_exception_reports_its_own_code_and_status()
    {
        var (status, body) = await ExecuteAsync(OgcErrorMapper.Map(OgcServiceException.NotDefined("missing")));

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Contains("LayerNotDefined", body);
    }

    [Theory]
    [InlineData(SpatialException.InvalidArguments, StatusCodes.Status400BadRequest, "InvalidParameterValue")]
    [InlineData(SpatialException.NotFound, StatusCodes.Status404NotFound, "LayerNotDefined")]
    [InlineData(SpatialException.StoreUnavailable, StatusCodes.Status503ServiceUnavailable, "NoApplicableCode")]
    public async Task A_spatial_exception_maps_by_code(string code, int status, string reportCode)
    {
        var (actualStatus, body) = await ExecuteAsync(OgcErrorMapper.Map(new SpatialException(code, "boom")));

        Assert.Equal(status, actualStatus);
        Assert.Contains(reportCode, body);
    }

    [Fact]
    public async Task An_unknown_spatial_code_is_an_internal_error()
    {
        var (status, body) = await ExecuteAsync(OgcErrorMapper.MapSpatial(new SpatialException("weird.code", "boom")));

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Contains("NoApplicableCode", body);
    }

    [Fact]
    public async Task Cancellation_reports_499()
    {
        var (status, _) = await ExecuteAsync(OgcErrorMapper.Map(new OperationCanceledException()));

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, status);
    }

    [Fact]
    public async Task An_unexpected_exception_is_an_internal_error()
    {
        var (status, body) = await ExecuteAsync(OgcErrorMapper.Map(new InvalidOperationException("boom")));

        Assert.Equal(StatusCodes.Status500InternalServerError, status);
        Assert.Contains("NoApplicableCode", body);
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, body);
    }
}
