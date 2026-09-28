using Spatial.Core.Geometry;

namespace Spatial.Ingest.Codec;

/// <summary>
/// The decode's route to a coordinate transform (ADR-0041 §4). The codec
/// references <c>Spatial.Core</c> only, so it cannot see
/// <c>ICoordinateTransforms</c> from the contracts and does not try: it
/// declares the one operation it needs and the host adapts the engine's
/// transform service onto it. A decode with no reprojector still reads a
/// declared CRS — it simply refuses to transform, rather than pretending the
/// data was already in the target CRS.
/// </summary>
public interface IIngestReprojection
{
    /// <summary>
    /// Transforms <paramref name="geometry"/> from <paramref name="source"/> to
    /// <paramref name="target"/>, both as <c>EPSG:{code}</c> strings. Must
    /// honour <paramref name="cancellationToken"/>.
    /// </summary>
    IGeometry Reproject(IGeometry geometry, string source, string target, CancellationToken cancellationToken = default);
}
