using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Contracts.Transformations;

namespace Spatial.Host.Api;

/// <summary>Typed CRS/transform routes (ADR-0033).</summary>
internal static class TransformEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/crs/describe", Describe)
            .Produces<CrsDescription>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        app.MapPost("/api/coordinates/transform", Transform)
            .Produces<GeometryResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
    }

    private static IResult Describe(DescribeRequest request, ICrsDirectory directory, CancellationToken token)
    {
        try
        {
            return Results.Ok(directory.Describe(request.Crs, token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static IResult Transform(TransformRequest request, ICoordinateTransforms transforms, CancellationToken token)
    {
        try
        {
            var geometry = CodecWire.DecodeGeometry(request.Geometry, "geometry");
            var result = transforms.Transform(geometry, request.Source, request.Target, token);
            return Results.Ok(new GeometryResponse(CodecWire.EncodeGeometry(result)));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }
}
