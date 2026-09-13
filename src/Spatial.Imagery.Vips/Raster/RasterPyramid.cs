namespace Spatial.Imagery.Vips.Raster;

/// <summary>
/// Pure pyramid-level math for the raster export pipeline (ADR-0051, plan I4):
/// which internal overview to read for a downscaled export, and how to map a
/// full-resolution pixel window onto that overview. Kept free of NetVips so
/// the decisions are unit-testable.
/// </summary>
internal static class RasterPyramid
{
    /// <summary>
    /// The coarsest overview level (0 = full resolution, n = the n-th
    /// half-resolution overview) whose pixels still cover the requested output
    /// size. It never upscales: it stops as soon as the next level would be
    /// smaller than the output. <paramref name="sourceWidth"/>/<paramref name="sourceHeight"/>
    /// are the full-resolution window being exported, not the whole image.
    /// </summary>
    public static int Select(int maxLevel, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        var level = 0;
        while (level < maxLevel)
        {
            var next = level + 1;
            if ((sourceWidth >> next) < outputWidth || (sourceHeight >> next) < outputHeight)
            {
                break;
            }

            level = next;
        }

        return level;
    }

    /// <summary>
    /// Maps a full-resolution pixel window onto an overview with the given
    /// dimensions, rounding outwards and clamping to the overview grid so the
    /// same geographic area is always covered.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) ScaleWindow(
        (int Left, int Top, int Width, int Height) window,
        int sourceWidth,
        int sourceHeight,
        int levelWidth,
        int levelHeight)
    {
        var scaleX = (double)levelWidth / sourceWidth;
        var scaleY = (double)levelHeight / sourceHeight;
        var left = (int)Math.Floor(window.Left * scaleX);
        var top = (int)Math.Floor(window.Top * scaleY);
        var right = (int)Math.Ceiling((window.Left + window.Width) * scaleX);
        var bottom = (int)Math.Ceiling((window.Top + window.Height) * scaleY);

        left = Math.Clamp(left, 0, levelWidth - 1);
        top = Math.Clamp(top, 0, levelHeight - 1);
        var width = Math.Clamp(right - left, 1, levelWidth - left);
        var height = Math.Clamp(bottom - top, 1, levelHeight - top);
        return (left, top, width, height);
    }
}
