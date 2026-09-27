using Spatial.Contracts;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The lossy half of the query response projection (spec §9.1.4): the
/// <c>maxAllowableOffset</c> allowance and the <c>quantizationParameters</c>
/// grid. Both are the caller stating how far the answer may be from the true
/// geometry, so both go through the one engine verb that states the same thing
/// — <see cref="IGeometryOperations.Generalize"/> — rather than through the
/// geometry service's <c>simplify</c>, whose tolerance is an algorithm
/// parameter (ADR-0075).
///
/// The order is the wire order: the offset is spent first, because it is
/// stated in the geometry's own units, and the quantum grid is snapped onto
/// what is left. The composed deviation is therefore at most the offset plus
/// half the quantum, and the quantization grid is what the client can index
/// against.
/// </summary>
internal static class GeometryGeneralization
{
    /// <summary>Applies the allowance and the grid, in that order.</summary>
    internal static IGeometry Generalize(
        IGeometry geometry,
        EsriFeatureQuery query,
        IGeometryOperations operations,
        CancellationToken cancellationToken)
    {
        var result = query.MaxAllowableOffset is { } offset
            ? operations.Generalize(geometry, offset, cancellationToken)
            : geometry;
        return query.Quantization is { } quantization
            ? operations.Generalize(quantization.Quantize(result), quantization.Tolerance, cancellationToken)
            : result;
    }
}
