using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.Rendering.Skia.Drawing;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Pipeline;

/// <summary>
/// Builds one symbol layer's candidates: filter and place the geometry, then
/// resolve the MapLibre <c>text-field</c>/<c>icon-image</c> templates from
/// feature attributes. Candidates are ordered by feature identity and source
/// envelope centre so page order from the store cannot change which label
/// wins a collision (ADR-0049). It adds no spatial algorithm.
/// </summary>
internal sealed class SymbolSceneBuilder
{
    private readonly ICoordinateTransforms _transforms;
    private readonly IGeometryOperations _operations;

    public SymbolSceneBuilder(ICoordinateTransforms transforms, IGeometryOperations operations)
    {
        _transforms = transforms;
        _operations = operations;
    }

    public SceneLayer Build(
        DrawLayer layer, LayerFeatures features, RasterViewport viewport, SymbolPaint symbol, CancellationToken cancellationToken)
    {
        var candidates = Candidates(layer, features);
        var symbols = new List<SymbolFeature>(candidates.Count);
        foreach (var (feature, geometry) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Resolve(symbol.Options, feature, geometry, features, viewport, cancellationToken) is { } resolved)
            {
                symbols.Add(resolved);
            }
        }

        return new SceneLayer(layer, []) { Symbols = symbols };
    }

    private static List<(IFeature Feature, IGeometry Geometry)> Candidates(DrawLayer layer, LayerFeatures features)
    {
        var candidates = new List<(IFeature Feature, IGeometry Geometry)>();
        foreach (var feature in features.Features)
        {
            if (layer.Filter.Matches(feature) && FeatureGeometry(feature, features.GeometryColumn) is { } geometry)
            {
                candidates.Add((feature, geometry));
            }
        }

        candidates.Sort(SymbolCandidateOrder.Compare);
        return candidates;
    }

    private SymbolFeature? Resolve(
        SymbolOptions options,
        IFeature feature,
        IGeometry geometry,
        LayerFeatures features,
        RasterViewport viewport,
        CancellationToken cancellationToken)
    {
        var placed = GeometryPipeline.Place(geometry, features, viewport, _transforms, _operations, cancellationToken);
        if (GeometryPipeline.SimplifyAndCull(placed, 0, viewport.Bounds, _operations, cancellationToken) is not { } shaped)
        {
            return null;
        }

        var text = SymbolTemplateResolver.Resolve(options.TextField, feature);
        var icon = options.IconImage is { } template ? SymbolTemplateResolver.Resolve(template, feature) : null;
        return text is not null || icon is not null ? new SymbolFeature(shaped, text, icon) : null;
    }

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
