using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Host.Api;

namespace Spatial.Host.Tests;

/// <summary>
/// The service-failure → HTTP-status mapping (ADR-0033): every
/// <see cref="SpatialException"/> code and the cancellation/generic
/// fallbacks map to the documented status without leaking internals.
/// </summary>
public sealed class ErrorMapperTests
{
    [Fact]
    public void Invalid_arguments_map_to_bad_request()
    {
        var result = Assert.IsType<BadRequest<ErrorResponse>>(ErrorMapper.Map(SpatialException.BadArguments("nope")));

        Assert.Equal(SpatialException.InvalidArguments, result.Value!.Code);
    }

    [Fact]
    public void Missing_datasets_map_to_not_found()
    {
        var result = Assert.IsType<NotFound<ErrorResponse>>(ErrorMapper.Map(SpatialException.Missing("gone")));

        Assert.Equal(SpatialException.NotFound, result.Value!.Code);
    }

    [Fact]
    public void Unavailable_stores_map_to_service_unavailable()
    {
        var result = Assert.IsType<JsonHttpResult<ErrorResponse>>(ErrorMapper.Map(SpatialException.Unavailable("down")));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(SpatialException.StoreUnavailable, result.Value!.Code);
    }

    [Fact]
    public void Unknown_spatial_codes_map_to_internal_error()
    {
        var result = Assert.IsType<JsonHttpResult<ErrorResponse>>(ErrorMapper.Map(new SpatialException("custom.code", "boom")));

        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Equal("custom.code", result.Value!.Code);
    }

    [Fact]
    public void Cancellation_maps_to_client_closed_request()
    {
        var result = Assert.IsType<StatusCodeHttpResult>(ErrorMapper.Map(new OperationCanceledException()));

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, result.StatusCode);
    }

    [Fact]
    public void Unexpected_failures_map_to_provider_failure_without_details()
    {
        var result = Assert.IsType<JsonHttpResult<ErrorResponse>>(ErrorMapper.Map(new InvalidOperationException("boom")));

        Assert.Equal(StatusCodes.Status500InternalServerError, result.StatusCode);
        Assert.Equal("provider.failure", result.Value!.Code);
    }
}
