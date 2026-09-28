using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Rendering.Skia.Drawing;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Pipeline;

/// <summary>
/// Builds the ordered <see cref="RenderScene"/> from a compiled style: resolve
/// each dataset's keyed services, push the viewport bbox down once per dataset,
/// place and shape the geometry with the injected engine services, resolve the
/// data-driven paint of each feature, and keep the style's bottom-to-top layer
/// order. Symbol layers are delegated to <see cref="SymbolSceneBuilder"/>. It
/// adds no spatial algorithm.
/// </summary>
internal sealed class SceneBuilder
{
    private readonly FeaturePipeline _features;
    private readonly GeometryLayerBuilder _geometry;
    private readonly SymbolSceneBuilder _symbols;

    public SceneBuilder(ICoordinateTransforms transforms, IGeometryOperations operations)
    {
        _features = new FeaturePipeline(transforms);
        _geometry = new GeometryLayerBuilder(transforms, operations);
        _symbols = new SymbolSceneBuilder(transforms, operations);
    }

    /// <summary>Builds the scene, reporting expression-evaluation counters when asked.</summary>
    public async Task<RenderScene> BuildAsync(
        CompiledStyle style,
        IReadOnlyList<MapLayerSource> layers,
        RasterViewport viewport,
        RenderStatistics? statistics,
        CancellationToken cancellationToken)
    {
        var sources = IndexLayers(layers);
        var zoom = ZoomEstimator.ZoomLevel(viewport);
        var scopes = new FeatureScopeCache(zoom, statistics);
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
                background = ((BackgroundPaint)layer.Paint.Resolve(new ExpressionScope(null, null, zoom))).Color;
                continue;
            }

            var source = FindSource(sources, layer);
            if (!cache.TryGetValue(layer.Dataset!, out var features))
            {
                features = await _features.ReadAsync(source, viewport, cancellationToken);
                cache[layer.Dataset!] = features;
            }

            var draw = new LayerDraw(features, viewport, scopes, cancellationToken);
            sceneLayers.Add(layer.Paint is SymbolPaint
                ? _symbols.Build(layer, draw)
                : _geometry.Build(layer, draw));
        }

        scopes.Report();
        return new RenderScene(sceneLayers, background);
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
}

