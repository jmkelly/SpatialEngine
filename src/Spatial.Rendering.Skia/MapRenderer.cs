using Spatial.PluginSdk;
using Spatial.Rendering.Skia.Drawing;
using Spatial.Rendering.Skia.Pipeline;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia;

/// <summary>
/// The raster pipeline facade (ADR-0044): compile the style, build the draw
/// scene from the resolved datasets, rasterize with Skia and encode through
/// <see cref="IRasterOperations"/>. It contains no spatial algorithm, no
/// service location and no drawing; the host resolves each layer's store and
/// catalogue at the edge and passes them in.
/// </summary>
public sealed class MapRenderer : IMapRenderer
{
    private readonly StyleCompiler _compiler = new();
    private readonly SceneBuilder _sceneBuilder;
    private readonly RasterComposer _composer;
    private readonly RenderLimits _limits;

    public MapRenderer(
        ICoordinateTransforms transforms,
        IGeometryOperations operations,
        IRasterOperations? rasterOperations = null,
        RenderLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(transforms);
        ArgumentNullException.ThrowIfNull(operations);
        _sceneBuilder = new SceneBuilder(transforms, operations);
        _composer = new RasterComposer(rasterOperations);
        _limits = limits ?? RenderLimits.Default;
    }

    public async Task<RasterImage> RenderAsync(MapRenderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var viewport = RenderValidator.Resolve(request, _limits);
        var style = _compiler.Compile(request.Style);
        if (DpiScaling.NeedsScaling(request.Dpi))
        {
            style = DpiScaling.Apply(style, request.Dpi);
        }
        var scene = CanvasBackground.Apply(
            await _sceneBuilder.BuildAsync(style, request.Layers, viewport, cancellationToken), request);
        var buffer = SkiaVectorRasterizer.Render(scene, viewport);
        return await _composer.ComposeAsync(buffer, request with { Viewport = viewport }, cancellationToken);
    }
}
