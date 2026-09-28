using System.Collections.Concurrent;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Pipeline;

/// <summary>
/// The per-feature expression scopes of one render (T-u2x.19). A scope is
/// render-invariant — the feature, its geometry type and the viewport zoom do
/// not change between layers — so the cache holds one per feature for the
/// whole style: an expression read by five properties across four layers is
/// evaluated once, not twenty times. When the render is measuring, the
/// counters are reported to <see cref="RenderStatistics"/> so the cache is
/// measured rather than assumed.
/// </summary>
internal sealed class FeatureScopeCache
{
    private readonly ConcurrentDictionary<IFeature, ExpressionScope> _scopes =
        new(ReferenceEqualityComparer.Instance);
    private readonly double _zoom;
    private readonly RenderStatistics? _statistics;

    public FeatureScopeCache(double zoom, RenderStatistics? statistics = null)
    {
        _zoom = zoom;
        _statistics = statistics;
    }

    /// <summary>How many feature scopes the render created.</summary>
    public int Count => _scopes.Count;

    /// <summary>The scope for one feature and its geometry, created on first use.</summary>
    public ExpressionScope For(IFeature feature, IGeometry? geometry) =>
        _scopes.GetOrAdd(feature, key => new ExpressionScope(key, geometry, _zoom));

    /// <summary>Reports the per-feature counters on the render statistics.</summary>
    public void Report()
    {
        if (_statistics is null)
        {
            return;
        }

        _statistics.Features = _scopes.Count;
        foreach (var scope in _scopes.Values)
        {
            _statistics.Evaluations += scope.Evaluations;
            _statistics.MemoHits += scope.MemoHits;
        }
    }
}

/// <summary>
/// Everything a layer's draw loop needs from the render it belongs to: the
/// dataset's resolved features, the viewport, the render-wide expression
/// scopes and the cancellation token. One context per layer, so the draw loops
/// do not carry four parameters each.
/// </summary>
internal sealed record LayerDraw(
    LayerFeatures Features,
    RasterViewport Viewport,
    FeatureScopeCache Scopes,
    CancellationToken CancellationToken)
{
    /// <summary>The draw loop's cancellation token.</summary>
    public CancellationToken Token => CancellationToken;
}

/// <summary>
/// What one render cost in expression evaluation. Internal and off the
/// contract: it exists so the per-feature cache is measurable from a test and
/// a benchmark, not a claim in a comment.
/// </summary>
internal sealed class RenderStatistics
{
    /// <summary>Feature scopes created (one per feature, not per layer).</summary>
    public int Features { get; set; }

    /// <summary>Expression nodes actually evaluated.</summary>
    public int Evaluations { get; set; }

    /// <summary>Evaluations served from a per-feature memo instead of re-running a node.</summary>
    public int MemoHits { get; set; }

    public override string ToString() =>
        FormattableString.Invariant($"{Features} features, {Evaluations} evaluations, {MemoHits} memo hits");
}
