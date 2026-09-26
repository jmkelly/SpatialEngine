using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Contracts.Transformations;
using Spatial.Core.Geometry;

namespace Spatial.Client;

/// <summary>
/// The geometry surface of the .NET client: the host's geometry operations
/// (buffer, intersection, validate, simplify), the coordinate transform and
/// the CRS description, all in and out as core geometry values (canonical
/// SGEOM Base64 on the wire). Split from <see cref="SpatialClient"/> so the
/// client's fan-out stays deliberate (ADR-0040); reach it through
/// <see cref="SpatialClient.Geometry"/>.
/// </summary>
public sealed class SpatialGeometryClient
{
    private readonly SpatialClientTransport _transport;

    internal SpatialGeometryClient(SpatialClientTransport transport) => _transport = transport;

    /// <summary>Buffers a geometry by a distance in the host's working units.</summary>
    public async Task<IGeometry> BufferAsync(IGeometry geometry, double distance, int quadrantSegments = 8, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var response = await _transport.PostAsync<GeometryResponse>(
            "/api/geometry/buffer",
            new BufferRequest(SpatialClientCodec.Encode(geometry), distance, quadrantSegments),
            cancellationToken);
        return SpatialClientCodec.Decode(response.Geometry);
    }

    /// <summary>Intersects two geometries.</summary>
    public async Task<IGeometry> IntersectionAsync(IGeometry left, IGeometry right, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var response = await _transport.PostAsync<GeometryResponse>(
            "/api/geometry/intersection",
            new IntersectionRequest(SpatialClientCodec.Encode(left), SpatialClientCodec.Encode(right)),
            cancellationToken);
        return SpatialClientCodec.Decode(response.Geometry);
    }

    /// <summary>Reports whether a geometry is valid, without throwing on an invalid one.</summary>
    public async Task<bool> ValidateAsync(IGeometry geometry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var response = await _transport.PostAsync<ValidateResponse>(
            "/api/geometry/validate", new ValidateRequest(SpatialClientCodec.Encode(geometry)), cancellationToken);
        return response.Valid;
    }

    /// <summary>Simplifies a geometry to a tolerance.</summary>
    public async Task<IGeometry> SimplifyAsync(IGeometry geometry, double tolerance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var response = await _transport.PostAsync<GeometryResponse>(
            "/api/geometry/simplify", new SimplifyRequest(SpatialClientCodec.Encode(geometry), tolerance), cancellationToken);
        return SpatialClientCodec.Decode(response.Geometry);
    }

    /// <summary>Describes a coordinate reference system.</summary>
    public Task<CrsDescription> DescribeAsync(string crs, CancellationToken cancellationToken = default) =>
        _transport.PostAsync<CrsDescription>("/api/crs/describe", new DescribeRequest(crs), cancellationToken);

    /// <summary>Transforms a geometry between coordinate reference systems.</summary>
    public async Task<IGeometry> TransformAsync(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var response = await _transport.PostAsync<GeometryResponse>(
            "/api/coordinates/transform", new TransformRequest(SpatialClientCodec.Encode(geometry), source, target), cancellationToken);
        return SpatialClientCodec.Decode(response.Geometry);
    }
}
