using Spatial.Core.Geometry;

namespace Spatial.Contracts;

/// <summary>
/// The standard geometry operations (ADR-0033; the versioned worker contracts are history): pure, synchronous
/// planar computations over core geometry values. Implementations keep
/// third-party types private (ADR-0005). Validation failures of the input
/// shape are <see cref="SpatialException"/> with code
/// <c>invalid.arguments</c>; an invalid geometry is a successful
/// <c>false</c> from <see cref="Validate"/>, never a failure.
/// </summary>
public interface IGeometryOperations
{
    IGeometry Buffer(IGeometry geometry, double distance, int quadrantSegments = 8, CancellationToken cancellationToken = default);

    IGeometry Intersection(IGeometry left, IGeometry right, CancellationToken cancellationToken = default);

    bool Validate(IGeometry geometry, CancellationToken cancellationToken = default);

    IGeometry Simplify(IGeometry geometry, double tolerance, CancellationToken cancellationToken = default);
}
