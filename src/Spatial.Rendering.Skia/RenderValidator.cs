using Spatial.Contracts;

namespace Spatial.Rendering.Skia;

/// <summary>
/// Resolves the effective viewport (applying the device pixel ratio) and
/// enforces the request's resource caps before any work is done. The DPI
/// is validated here as well: unlike <see cref="MapRenderRequest.Scale"/>
/// it never changes the output frame, it only scales symbology.
/// </summary>
internal static class RenderValidator
{
    public static RasterViewport Resolve(MapRenderRequest request, RenderLimits limits)
    {
        if (!double.IsFinite(request.Dpi) || request.Dpi <= 0)
        {
            throw SpatialException.BadArguments("'dpi' must be a positive number.");
        }

        var viewport = Scale(request);
        if (!viewport.IsValid)
        {
            throw SpatialException.BadArguments("The viewport requires a non-empty bounds and a positive pixel size.");
        }

        if ((long)viewport.Width * viewport.Height > limits.MaxPixels)
        {
            throw SpatialException.BadArguments(
                FormattableString.Invariant($"The viewport exceeds the {limits.MaxPixels}-pixel limit."));
        }

        if (request.Layers.Count > limits.MaxLayers)
        {
            throw SpatialException.BadArguments(
                FormattableString.Invariant($"The request lists {request.Layers.Count} layers, above the {limits.MaxLayers}-layer limit."));
        }

        return viewport;
    }

    private static RasterViewport Scale(MapRenderRequest request)
    {
        if (request.Scale <= 0)
        {
            throw SpatialException.BadArguments("'scale' must be positive.");
        }

        var viewport = request.Viewport;
        if (Math.Abs(request.Scale - 1) < 1e-9)
        {
            return viewport;
        }

        return viewport with
        {
            Width = Math.Max(1, (int)Math.Round(viewport.Width * request.Scale)),
            Height = Math.Max(1, (int)Math.Round(viewport.Height * request.Scale)),
        };
    }
}
