using Spatial.PluginSdk;
using Spatial.Rendering.Skia.Drawing;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia;

/// <summary>
/// Resolves the canvas background for a render. A style <c>background</c>
/// layer always wins; when there is no imagery the request's background
/// colour (or opaque white when <c>transparent</c> is false) is cleared
/// beneath the vector layers. With imagery the background belongs to the
/// compositor, so the vector buffer stays transparent.
/// </summary>
internal static class CanvasBackground
{
    private static readonly StyleColor OpaqueWhite = new(255, 255, 255);

    public static RenderScene Apply(RenderScene scene, MapRenderRequest request)
    {
        if (scene.Background is not null || request.Imagery is { Count: > 0 })
        {
            return scene;
        }

        if (!string.IsNullOrWhiteSpace(request.Background))
        {
            return scene with { Background = StyleColorParser.Parse(request.Background) };
        }

        return request.Transparent ? scene : scene with { Background = OpaqueWhite };
    }
}
