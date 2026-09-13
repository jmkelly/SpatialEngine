using Spatial.Core.Geometry;
using Spatial.PluginSdk;

namespace Spatial.Rendering.Skia.Pipeline;

/// <summary>
/// The place/shape stages of the render pipeline: transform the viewport
/// bounds into a dataset's source CRS for bbox pushdown, place geometry into
/// the viewport CRS, then simplify in screen units and cull to the viewport.
/// It adds no spatial algorithm of its own — it calls the injected engine
/// services (ADR-0044).
/// </summary>
internal static class GeometryPipeline
{
    /// <summary>Transforms the viewport bounds from <paramref name="from"/> into <paramref name="to"/> by transforming its corners.</summary>
    public static Envelope TransformEnvelope(
        ICoordinateTransforms transforms, Envelope bounds, string from, string to, CancellationToken cancellationToken)
    {
        if (bounds.IsEmpty || string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            return bounds;
        }

        var ring = GeometryFactory.CreateLineString(
        [
            new Coordinate(bounds.MinX, bounds.MinY),
            new Coordinate(bounds.MaxX, bounds.MinY),
            new Coordinate(bounds.MaxX, bounds.MaxY),
            new Coordinate(bounds.MinX, bounds.MaxY),
            new Coordinate(bounds.MinX, bounds.MinY),
        ]);
        return transforms.Transform(ring, from, to, cancellationToken).Envelope ?? bounds;
    }

    /// <summary>
    /// Places a source-CRS geometry into the viewport CRS. When the CRSs
    /// differ the geometry is first clipped to the source-CRS image of the
    /// viewport, so geometry reaching beyond the target projection's valid
    /// area (the poles in Web Mercator) is bounded before it is reprojected.
    /// </summary>
    public static IGeometry Place(
        IGeometry geometry, LayerFeatures features, RasterViewport viewport,
        ICoordinateTransforms transforms, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        var source = FormattableString.Invariant($"EPSG:{features.Srid}");
        if (string.Equals(source, viewport.Crs, StringComparison.OrdinalIgnoreCase))
        {
            return geometry;
        }

        var bounded = ClipToBounds(geometry, features.SourceBounds, operations, cancellationToken);
        return Project(bounded, features.Srid, viewport.Crs, transforms, cancellationToken);
    }

    /// <summary>
    /// Clips a geometry to <paramref name="bounds"/> when it reaches beyond
    /// them; a geometry already inside the bounds (or without an envelope) is
    /// returned unchanged, so the common case pays nothing.
    /// </summary>
    public static IGeometry ClipToBounds(
        IGeometry geometry, Envelope bounds, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        if (bounds.IsEmpty || geometry.Envelope is not { } envelope || Contains(bounds, envelope))
        {
            return geometry;
        }

        var clip = GeometryFactory.CreatePolygon(
        [
            new Coordinate(bounds.MinX, bounds.MinY),
            new Coordinate(bounds.MaxX, bounds.MinY),
            new Coordinate(bounds.MaxX, bounds.MaxY),
            new Coordinate(bounds.MinX, bounds.MaxY),
            new Coordinate(bounds.MinX, bounds.MinY),
        ]);
        return operations.Intersection(geometry, clip, cancellationToken);
    }

    private static bool Contains(Envelope outer, Envelope inner) =>
        inner.MinX >= outer.MinX && inner.MaxX <= outer.MaxX
        && inner.MinY >= outer.MinY && inner.MaxY <= outer.MaxY;

    /// <summary>Places a source-CRS geometry into the viewport CRS (a no-op when they already agree).</summary>
    public static IGeometry Project(
        IGeometry geometry, int sourceSrid, string target, ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        var source = FormattableString.Invariant($"EPSG:{sourceSrid}");
        return string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
            ? geometry
            : transforms.Transform(geometry, source, target, cancellationToken);
    }

    /// <summary>
    /// Simplifies in viewport units and drops geometry that falls outside the
    /// viewport. Returns <c>null</c> when there is nothing left to draw.
    /// </summary>
    public static IGeometry? SimplifyAndCull(
        IGeometry geometry, double tolerance, Envelope viewport, IGeometryOperations operations, CancellationToken cancellationToken)
    {
        var shaped = tolerance > 0 && geometry.CoordinateCount > 2
            ? operations.Simplify(geometry, tolerance, cancellationToken)
            : geometry;
        if (shaped.Envelope is not { } envelope || !envelope.Intersects(viewport))
        {
            return null;
        }

        return shaped;
    }
}
