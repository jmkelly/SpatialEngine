using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Rendering.Skia.Pipeline;

/// <summary>
/// Resolved source data for one dataset: source SRID, geometry column and
/// features, plus the source-CRS image of the viewport used to clip geometry
/// before reprojection.
/// </summary>
internal sealed record LayerFeatures(int Srid, string GeometryColumn, IReadOnlyList<IFeature> Features, Envelope SourceBounds);

/// <summary>
/// The read stage of the render pipeline: describe the dataset to learn its
/// source CRS and geometry column, push the viewport bbox down into the
/// keyed <see cref="IFeatureStore"/>, and flatten the returned pages.
/// </summary>
internal sealed class FeaturePipeline
{
    private readonly ICoordinateTransforms _transforms;

    public FeaturePipeline(ICoordinateTransforms transforms) => _transforms = transforms;

    public async Task<LayerFeatures> ReadAsync(MapLayerSource source, RasterViewport viewport, CancellationToken cancellationToken)
    {
        var description = await source.Catalogue.DescribeAsync(source.Dataset, cancellationToken);
        var sourceBounds = GeometryPipeline.TransformEnvelope(
            _transforms, viewport.Bounds, viewport.Crs, FormattableString.Invariant($"EPSG:{description.Srid}"), cancellationToken);
        var bbox = new BoundingBox(sourceBounds.MinX, sourceBounds.MinY, sourceBounds.MaxX, sourceBounds.MaxY);
        var batches = await source.Features.QueryAsync(source.Dataset, bbox, source.Filter, cancellationToken);
        var features = new List<IFeature>();
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var feature in batch.Features)
            {
                if (source.Time is null || MatchesTime(feature, source.Time))
                {
                    features.Add(feature);
                }
            }
        }

        return new LayerFeatures(description.Srid, description.GeometryColumn, features, sourceBounds);
    }

    /// <summary>
    /// Applies the render <see cref="MapTimeExtent"/> to one feature: it
    /// matches when any date value falls inside the (inclusive) bounds, where
    /// a <c>null</c> bound is infinite. A feature with no date values matches
    /// unconditionally. This mirrors the query path's temporal rule
    /// (<c>FeatureQueryEngine.MatchesTime</c> in the GeoServices adapter,
    /// which the renderer must not reference); the two are intentionally the
    /// same rule in the two places the engine reads features.
    /// </summary>
    private static bool MatchesTime(Feature feature, MapTimeExtent time)
    {
        var dated = false;
        foreach (var attribute in feature.Attributes)
        {
            if (attribute.Kind != AttributeKind.DateTimeOffset)
            {
                continue;
            }

            dated = true;
            var milliseconds = attribute.DateTimeOffsetValue.ToUnixTimeMilliseconds();
            if ((time.StartMs is null || milliseconds >= time.StartMs)
                && (time.EndMs is null || milliseconds <= time.EndMs))
            {
                return true;
            }
        }

        return !dated;
    }
}
