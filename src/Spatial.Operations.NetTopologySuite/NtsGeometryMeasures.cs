using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite.Adapters;
using Spatial.PluginSdk;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Spatial.Operations.NetTopologySuite;

/// <summary>
/// The NetTopologySuite measurement service (ADR-0036): an in-process
/// implementation of <see cref="IGeometryMeasures"/>. NTS types stay inside
/// this assembly (ADR-0005). Planar and cancellable.
/// </summary>
public sealed class NtsGeometryMeasures : IGeometryMeasures
{
    public double Area(IGeometry geometry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        cancellationToken.ThrowIfCancellationRequested();
        return Run(geometry, "area", nts => nts.Area);
    }

    public double Length(IGeometry geometry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        cancellationToken.ThrowIfCancellationRequested();
        return Run(geometry, "length", nts => nts.Length);
    }

    public double Distance(IGeometry left, IGeometry right, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return GeometryAdapter.ToNts(left).Distance(GeometryAdapter.ToNts(right));
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, "distance");
        }
    }

    public IGeometry LabelPoint(IGeometry geometry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return GeometryAdapter.ToCore(GeometryAdapter.ToNts(geometry).InteriorPoint, geometry.CoordinateReference);
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, "labelPoints");
        }
    }

    private static double Run(IGeometry geometry, string operation, Func<NtsGeometry, double> compute)
    {
        try
        {
            return compute(GeometryAdapter.ToNts(geometry));
        }
        catch (Exception exception)
        {
            throw NtsOperationErrors.Map(exception, operation);
        }
    }
}
