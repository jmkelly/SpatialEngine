using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
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

    public SceneLayer Build(DrawLayer layer, LayerDraw draw)
    {
        var candidates = Candidates(layer, draw);
        var symbols = new List<SymbolFeature>(candidates.Count);
        foreach (var (feature, geometry) in candidates)
        {
            draw.Token.ThrowIfCancellationRequested();
            if (Resolve(layer, feature, geometry, draw) is { } resolved)
            {
                symbols.Add(resolved);
            }
        }

        return new SceneLayer(layer, []) { Symbols = symbols };
    }

    private static List<(IFeature Feature, IGeometry Geometry)> Candidates(DrawLayer layer, LayerDraw draw)
    {
        var candidates = new List<(IFeature Feature, IGeometry Geometry)>();
        foreach (var feature in draw.Features.Features)
        {
            if (FeatureGeometry(feature, draw.Features.GeometryColumn) is { } geometry
                && layer.Filter.Matches(draw.Scopes.For(feature, geometry)))
            {
                candidates.Add((feature, geometry));
            }
        }

        candidates.Sort(SymbolCandidateOrder.Compare);
        return candidates;
    }

    private SymbolFeature? Resolve(DrawLayer layer, IFeature feature, IGeometry geometry, LayerDraw draw)
    {
        var placed = GeometryPipeline.Place(geometry, draw.Features, draw.Viewport, _transforms, _operations, draw.Token);
        if (GeometryPipeline.SimplifyAndCull(placed, 0, draw.Viewport.Bounds, _operations, draw.Token) is not { } shaped)
        {
            return null;
        }

        var symbol = (SymbolPaint)layer.Paint;
        var text = SymbolTemplateResolver.Resolve(symbol.Options.TextField, feature);
        var icon = symbol.Options.IconImage is { } template ? SymbolTemplateResolver.Resolve(template, feature) : null;
        if (text is null && icon is null)
        {
            return null;
        }

        var options = symbol.Expressions is null
            ? null
            : ((SymbolPaint)symbol.Resolve(draw.Scopes.For(feature, geometry))).Options;
        return new SymbolFeature(shaped, text, icon) { Options = options };
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
