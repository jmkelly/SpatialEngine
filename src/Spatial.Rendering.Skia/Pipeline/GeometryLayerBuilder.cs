using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Rendering.Skia.Drawing;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Pipeline;

/// <summary>
/// Builds one geometry layer's draw list: for each feature, read its geometry,
/// test the layer's filter, place and shape it, and — when the layer's paint is
/// data-driven — resolve the paint for that feature. The features that resolved
/// to the same paint are batched by the rasterizer, so a step or match ramp
/// keeps the path batching the constant path has. It adds no spatial algorithm:
/// placement and shaping are <see cref="GeometryPipeline"/>'s (ADR-0044).
/// </summary>
internal sealed class GeometryLayerBuilder(
    ICoordinateTransforms transforms, IGeometryOperations operations)
{
    public SceneLayer Build(DrawLayer layer, LayerDraw draw)
    {
        var viewport = draw.Viewport;
        var tolerance = viewport.UnitsPerPixel / 2;
        var geometries = new List<IGeometry>();
        var runs = new List<PaintRun>();
        foreach (var feature in draw.Features.Features)
        {
            draw.Token.ThrowIfCancellationRequested();
            if (FeatureGeometry(feature, draw.Features.GeometryColumn) is not { } geometry)
            {
                continue;
            }

            var scope = draw.Scopes.For(feature, geometry);
            if (!layer.Filter.Matches(scope))
            {
                continue;
            }

            var placed = GeometryPipeline.Place(geometry, draw.Features, viewport, transforms, operations, draw.Token);
            if (GeometryPipeline.SimplifyAndCull(placed, tolerance, viewport.Bounds, operations, draw.Token) is not { } shaped)
            {
                continue;
            }

            geometries.Add(shaped);
            if (layer.Paint.IsDataDriven())
            {
                runs.Add(new PaintRun(shaped, layer.Paint.Resolve(scope)));
            }
        }

        return new SceneLayer(layer, geometries) { Runs = runs };
    }

    /// <summary>The dataset's geometry for one feature, or <c>null</c> when it has none.</summary>
    public static IGeometry? FeatureGeometry(IFeature feature, string column)
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
