using Spatial.Contracts;
using Spatial.Contracts.Http;

namespace Spatial.Host.Api;

/// <summary>Typed geometry-operation routes (ADR-0033): one POST per verb, Base64 SGEOM in and out.</summary>
internal static class GeometryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/geometry/buffer", Buffer)
            .Produces<GeometryResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        app.MapPost("/api/geometry/intersection", Intersection)
            .Produces<GeometryResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
        app.MapPost("/api/geometry/validate", Validate)
            .Produces<ValidateResponse>();
        app.MapPost("/api/geometry/simplify", Simplify)
            .Produces<GeometryResponse>()
            .Produces<ErrorResponse>(StatusCodes.Status400BadRequest);
    }

    private static IResult Buffer(BufferRequest request, IGeometryOperations operations, CancellationToken token)
    {
        try
        {
            var geometry = CodecWire.DecodeGeometry(request.Geometry, "geometry");
            var result = operations.Buffer(geometry, request.Distance, request.QuadrantSegments ?? 8, token);
            return Results.Ok(new GeometryResponse(CodecWire.EncodeGeometry(result)));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static IResult Intersection(IntersectionRequest request, IGeometryOperations operations, CancellationToken token)
    {
        try
        {
            var left = CodecWire.DecodeGeometry(request.Left, "left");
            var right = CodecWire.DecodeGeometry(request.Right, "right");
            var result = operations.Intersection(left, right, token);
            return Results.Ok(new GeometryResponse(CodecWire.EncodeGeometry(result)));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static IResult Validate(ValidateRequest request, IGeometryOperations operations, CancellationToken token)
    {
        try
        {
            var geometry = CodecWire.DecodeGeometry(request.Geometry, "geometry");
            return Results.Ok(new ValidateResponse(operations.Validate(geometry, token)));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static IResult Simplify(SimplifyRequest request, IGeometryOperations operations, CancellationToken token)
    {
        try
        {
            var geometry = CodecWire.DecodeGeometry(request.Geometry, "geometry");
            var result = operations.Simplify(geometry, request.Tolerance, token);
            return Results.Ok(new GeometryResponse(CodecWire.EncodeGeometry(result)));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }
}
