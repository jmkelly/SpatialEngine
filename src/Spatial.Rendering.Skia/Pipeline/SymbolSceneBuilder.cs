using System.Globalization;
using System.Text;
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

        candidates.Sort(CompareCandidates);
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
        var placed = GeometryPipeline.Project(geometry, features.Srid, viewport.Crs, _transforms, cancellationToken);
        if (GeometryPipeline.SimplifyAndCull(placed, 0, viewport.Bounds, _operations, cancellationToken) is not { } shaped)
        {
            return null;
        }

        var text = ResolveTemplate(options.TextField, feature);
        var icon = options.IconImage is { } template ? ResolveTemplate(template, feature) : null;
        return text is not null || icon is not null ? new SymbolFeature(shaped, text, icon) : null;
    }

    /// <summary>Orders candidates by identity then source envelope centre (a deterministic tie-break).</summary>
    private static int CompareCandidates((IFeature Feature, IGeometry Geometry) left, (IFeature Feature, IGeometry Geometry) right)
    {
        var byId = string.CompareOrdinal(left.Feature.Id.Value, right.Feature.Id.Value);
        if (byId != 0)
        {
            return byId;
        }

        var (leftX, leftY) = Centre(left.Geometry);
        var (rightX, rightY) = Centre(right.Geometry);
        var byX = leftX.CompareTo(rightX);
        return byX != 0 ? byX : leftY.CompareTo(rightY);
    }

    private static (double X, double Y) Centre(IGeometry geometry) =>
        geometry.Envelope is { } envelope
            ? ((envelope.MinX + envelope.MaxX) / 2, (envelope.MinY + envelope.MaxY) / 2)
            : (double.NaN, double.NaN);

    /// <summary>
    /// Expands a MapLibre <c>text-field</c>/<c>icon-image</c> template:
    /// <c>{attribute}</c> tokens are replaced by attribute text. Returns
    /// <c>null</c> when a token names a missing attribute, which skips that
    /// part of the symbol (never a half-rendered label).
    /// </summary>
    private static string? ResolveTemplate(string template, IFeature feature)
    {
        if (template.Length == 0)
        {
            return null;
        }

        var builder = new StringBuilder(template.Length);
        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            builder.Append(template, index, open - index);
            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                builder.Append(template, open, template.Length - open);
                break;
            }

            if (ReadAttribute(feature, template[(open + 1)..close]) is not { } text)
            {
                return null;
            }

            builder.Append(text);
            index = close + 1;
        }

        return builder.ToString();
    }

    internal static string? ReadAttribute(IFeature feature, string name)
    {
        var index = feature.Schema.IndexOf(name);
        if (index < 0)
        {
            return null;
        }

        var value = feature[index];
        return value.Kind switch
        {
            AttributeKind.String => value.StringValue,
            AttributeKind.Int64 => value.Int64Value.ToString(CultureInfo.InvariantCulture),
            AttributeKind.Double => value.DoubleValue.ToString(CultureInfo.InvariantCulture),
            AttributeKind.Boolean => value.BooleanValue ? "true" : "false",
            AttributeKind.Guid => value.GuidValue.ToString(),
            AttributeKind.DateTimeOffset => value.DateTimeOffsetValue.ToString("O", CultureInfo.InvariantCulture),
            _ => null,
        };
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
