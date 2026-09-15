using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite.Adapters;

namespace Spatial.Operations.NetTopologySuite;

/// <summary>
/// The NetTopologySuite spatial-relation service (ADR-0036): an in-process
/// implementation of <see cref="IGeometryRelations"/> over the OGC DE-9IM
/// relate operation. NTS types stay inside this assembly (ADR-0005).
/// </summary>
public sealed class NtsGeometryRelations : IGeometryRelations
{
    public bool Relate(IGeometry left, IGeometry right, string intersectionPattern, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (string.IsNullOrWhiteSpace(intersectionPattern))
        {
            throw SpatialException.BadArguments("'intersectionPattern' must be a DE-9IM pattern such as 'T*T***T**'.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return GeometryAdapter.ToNts(left).Relate(GeometryAdapter.ToNts(right), intersectionPattern);
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, "relation");
        }
    }
}
