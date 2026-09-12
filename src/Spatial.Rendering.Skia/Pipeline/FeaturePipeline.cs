using Spatial.Core.Features;
using Spatial.PluginSdk;

namespace Spatial.Rendering.Skia.Pipeline;

/// <summary>Resolved source data for one dataset: source SRID, geometry column and features.</summary>
internal sealed record LayerFeatures(int Srid, string GeometryColumn, IReadOnlyList<IFeature> Features);

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
            features.AddRange(batch.Features);
        }

        return new LayerFeatures(description.Srid, description.GeometryColumn, features);
    }
}
