using Spatial.Core.Geometry;
using Spatial.PluginSdk.Transformations;

namespace Spatial.PluginSdk;

/// <summary>
/// CRS description plus coordinate transformation (ADR-0033; the versioned worker contracts are history).
/// Axis order is x-first for every CRS (x = longitude/easting).
/// </summary>
public interface ICrsDirectory
{
    CrsDescription Describe(string crs, CancellationToken cancellationToken = default);
}

public interface ICoordinateTransforms
{
    IGeometry Transform(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default);
}
