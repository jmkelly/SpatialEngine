using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.Rendering.Skia.Drawing;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Pipeline;

/// <summary>
/// Builds the ordered <see cref="RenderScene"/> from a compiled style: resolve
/// each dataset's keyed services, push the viewport bbox down once per dataset,
/// place and shape the geometry with the injected engine services, and keep
/// the style's bottom-to-top layer order. It adds no spatial algorithm.
/// </summary>
internal sealed class SceneBuilder
{
    private readonly ICoordinateTransforms _transforms;
    private readonly IGeometryOperations _operations;
    private readonly FeaturePipeline _features;

    public SceneBuilder(ICoordinateTransforms transforms, IGeometryOperations operations)
    {
        _transforms = transforms;
        _operations = operations;
        _features = new FeaturePipeline(transforms);
    }

    public async Task<RenderScene> BuildAsync(
        CompiledStyle style,
        IReadOnlyList<MapLayerSource> layers,
        RasterViewport viewport,
        CancellationToken cancellationToken)
    {
        var sources = IndexLayers(layers);
        var zoom = ZoomEstimator.ZoomLevel(viewport);
        var cache = new Dictionary<string, LayerFeatures>(StringComparer.Ordinal);
        var sceneLayers = new List<SceneLayer>();
        StyleColor? background = null;

        foreach (var layer in style.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!layer.IsVisibleAt(zoom))
            {
                continue;
            }

            if (layer.Kind == DrawKind.Background)
            {
                background = ((BackgroundPaint)layer.Paint).Color;
                continue;
            }

            var source = FindSource(sources, layer);
            if (!cache.TryGetValue(layer.Dataset!, out var features))
            {
                features = await _features.ReadAsync(source, viewport, cancellationToken);
                cache[layer.Dataset!] = features;
            }

            sceneLayers.Add(BuildSceneLayer(layer, features, viewport, cancellationToken));
        }

        return new RenderScene(sceneLayers, background);
    }

    private SceneLayer BuildSceneLayer(
        DrawLayer layer, LayerFeatures features, RasterViewport viewport, CancellationToken cancellationToken)
    {
        var tolerance = viewport.UnitsPerPixel / 2;
        var geometries = new List<IGeometry>();
        foreach (var feature in features.Features)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!layer.Filter.Matches(feature) || FeatureGeometry(feature, features.GeometryColumn) is not { } geometry)
            {
                continue;
            }

            var placed = GeometryPipeline.Project(geometry, features.Srid, viewport.Crs, _transforms, cancellationToken);
            if (GeometryPipeline.SimplifyAndCull(placed, tolerance, viewport.Bounds, _operations, cancellationToken) is { } shaped)
            {
                geometries.Add(shaped);
            }
        }

        return new SceneLayer(layer, geometries);
    }

    private static Dictionary<string, MapLayerSource> IndexLayers(IReadOnlyList<MapLayerSource> layers)
    {
        var sources = new Dictionary<string, MapLayerSource>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            ArgumentNullException.ThrowIfNull(layer);
            if (!sources.TryAdd(layer.Dataset, layer))
            {
                throw SpatialException.BadArguments($"Dataset '{layer.Dataset}' is listed more than once.");
            }
        }

        return sources;
    }

    private static MapLayerSource FindSource(Dictionary<string, MapLayerSource> sources, DrawLayer layer) =>
        sources.TryGetValue(layer.Dataset!, out var source)
            ? source
            : throw SpatialException.BadArguments(
                $"Style layer '{layer.Id}' references dataset '{layer.Dataset}' which is not in the request.");

    private static IGeometry? FeatureGeometry(IFeature feature, string column)
    {
        var index = feature.Schema.IndexOf(column);
        if (index < 0)
        {
            return null;
        }

        var value = feature[index];
        return value.Kind == AttributeKind.Geometry ? value.GeometryValue : null;
    }
}
