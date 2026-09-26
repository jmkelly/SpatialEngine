using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Per-feature projection for query responses: <c>outSR</c> reprojection
/// and <c>geometryPrecision</c> rounding. Split out of
/// <see cref="FeatureQueryEngine"/> so the query facade keeps only
/// orchestration and the shaping fan-out lives with the code that uses
/// it (ADR-0040). The rounding itself lives in <see cref="GeometryRounding"/>.
/// </summary>
internal static class FeatureProjection
{
    internal static MatchedFeature TransformFeature(
        MatchedFeature match,
        EsriFeatureQuery query,
        CoordinateReference? layerCrs,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var feature = match.Feature;
        var geometryIndex = FeatureGeometry.Index(feature.Schema);
        if (geometryIndex < 0 || feature[geometryIndex].Kind != AttributeKind.Geometry)
        {
            return match;
        }

        var geometry = feature[geometryIndex].GeometryValue;
        if (query.OutSr is { } target && layerCrs is not null && target != layerCrs)
        {
            geometry = transforms.Transform(geometry, layerCrs.Value.ToString(), target.ToString(), cancellationToken);
        }

        if (query.GeometryPrecision is { } precision)
        {
            geometry = GeometryRounding.Round(geometry, precision);
        }
        else if (ReferenceEquals(geometry, feature[geometryIndex].GeometryValue))
        {
            return match;
        }

        var attributes = feature.Attributes.ToArray();
        attributes[geometryIndex] = AttributeValue.FromGeometry(geometry);
        return new MatchedFeature(match.ObjectId, new Feature(feature.Id, feature.Schema, attributes));
    }

    internal static IGeometry TransformGeometry(
        IGeometry geometry,
        CoordinateReference? source,
        CoordinateReference? target,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        if (target is not { } to || source is not { } from || from == to)
        {
            return geometry;
        }

        return transforms.Transform(geometry, from.ToString(), to.ToString(), cancellationToken);
    }

    /// <summary>
    /// Reprojects a query's own <c>geometry</c> into the layer's CRS so the
    /// spatial match runs in the coordinates the features are stored in.
    /// Returns the geometry unchanged when it is absent, the layer is
    /// unprojected, or the two already agree.
    /// </summary>
    internal static IGeometry? TransformQueryGeometry(
        IGeometry? geometry,
        CoordinateReference? layerCrs,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        if (geometry is null || layerCrs is null || geometry.CoordinateReference is not { } source || source == layerCrs)
        {
            return geometry;
        }

        return transforms.Transform(geometry, source.ToString(), layerCrs.Value.ToString(), cancellationToken);
    }
}
