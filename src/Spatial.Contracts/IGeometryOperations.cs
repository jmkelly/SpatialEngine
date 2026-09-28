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

    /// <summary>
    /// Douglas-Peucker generalization: the algorithm parameter. A vertex is
    /// dropped only when it lies within <paramref name="tolerance"/> of the
    /// chord that replaces it, so the tolerance is a perpendicular distance
    /// and the result may collapse a shape (a small ring at a large tolerance
    /// degenerates to a line or a point). Callers that must not lose a
    /// geometry's kind use <see cref="Generalize"/>.
    /// </summary>
    IGeometry Simplify(IGeometry geometry, double tolerance, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generalization by a deviation <em>allowance</em>: the caller states how
    /// far the answer may be from the true geometry, not how coarsely the
    /// algorithm should thin it.
    /// </summary>
    /// <returns>
    /// The input when the allowance cannot be spent — at zero, or at an
    /// allowance whose simplification would empty or reshape the geometry.
    /// Otherwise a geometry whose vertices are all vertices of the input and
    /// within <paramref name="maxDisplacement"/> of it, so the result never
    /// moves a feature. A non-finite or negative allowance is
    /// <c>invalid.arguments</c>.
    /// </returns>
    /// <remarks>
    /// This is the verb the query path serves <c>maxAllowableOffset</c> and
    /// the <c>quantizationParameters</c> quantum through, and it is distinct
    /// from <see cref="Simplify"/> precisely so the two parameter meanings
    /// cannot be swapped at a call site.
    /// </remarks>
    IGeometry Generalize(IGeometry geometry, double maxDisplacement, CancellationToken cancellationToken = default);
}
