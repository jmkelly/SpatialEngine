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
        var batches = (await source.Features.QueryAsync(
            source.Dataset, new FeatureQuery(BoundingBox: bbox, Where: source.Where), cancellationToken)).Batches;
        var features = new List<IFeature>();
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var feature in batch.Features)
            {
                if (source.Time is null || MatchesTime(feature, source.Dataset, source.Time, description.TimeFields))
                {
                    features.Add(feature);
                }
            }
        }

        return new LayerFeatures(description.Srid, description.GeometryColumn, features, sourceBounds);
    }

    /// <summary>
    /// Applies the render <see cref="MapTimeExtent"/> to one feature: a layer
    /// with a temporal designation is matched against its feature temporal
    /// extent (ADR-0175), and a layer without one matches when any date value
    /// falls inside the (inclusive) bounds, where a <c>null</c> bound is
    /// infinite, with a feature that has no date values matching
    /// unconditionally. Both rules are the query path's rules
    /// (<c>FeatureSpatialMatcher.MatchesTime</c> in the GeoServices adapter,
    /// which the renderer must not reference), so the render path and the query
    /// path cannot drift: the rule itself lives once in
    /// <see cref="TemporalExtent"/>.
    /// </summary>
    private static bool MatchesTime(IFeature feature, string dataset, MapTimeExtent time, TemporalExtentFields? fields)
    {
        var window = TemporalExtent.FromMilliseconds(time.StartMs, time.EndMs);
        if (fields is null)
        {
            if (time.Relation is not TemporalRelation.Overlaps)
            {
                throw SpatialException.BadArguments(
                    $"The temporal relation '{time.Relation}' is not supported for dataset '{dataset}': it designates no start/end date field, so a feature has no temporal extent to compare the requested window against.");
            }

            return TemporalExtent.MatchesAnyDate(feature, window);
        }

        return TemporalExtent.From(feature, fields).Matches(window, time.Relation);
    }
}
