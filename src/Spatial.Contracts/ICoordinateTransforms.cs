using Spatial.Contracts.Transformations;
using Spatial.Contracts.TransformationSearch;
using Spatial.Core.Geometry;

namespace Spatial.Contracts;

/// <summary>
/// CRS description plus coordinate transformation (ADR-0033; the versioned worker contracts are history).
/// Axis order is x-first for every CRS (x = longitude/easting).
/// </summary>
public interface ICrsDirectory
{
    CrsDescription Describe(string crs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches the provider's datum-transformation graph (ADR-0074): the
    /// candidate operations between two CRSs, each carrying its steps,
    /// parameters, area of use and stated accuracy, ranked best-accuracy
    /// first. An optional area of interest filters the candidates whose area
    /// of use covers it; a same-datum pair yields an empty list because no
    /// operation is needed. The first candidate is the path the provider
    /// itself applies, so an interop surface can name it back to a client.
    /// </summary>
    IReadOnlyList<CrsTransformation> FindTransformations(
        CrsTransformationQuery query,
        CancellationToken cancellationToken = default);
}

public interface ICoordinateTransforms
{
    IGeometry Transform(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default);
}
